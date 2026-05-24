using FocusAgent.Core.Realtime;
using FocusAgent.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FocusAgent.Core.Sessions;

/// <summary>
/// Pumps application-level <c>Heartbeat</c> hub invocations every
/// <see cref="SessionSettings.HeartbeatIntervalSeconds"/> while the student is
/// in a joined session. Subscribes to <see cref="SessionCoordinator.SessionJoined"/>
/// to start the loop and <see cref="SessionCoordinator.SessionLeft"/> to stop
/// it, so heartbeats only flow when there is a session to keep alive.
/// </summary>
public sealed class SessionHeartbeatService : IAsyncDisposable
{
    private readonly SessionCoordinator _coordinator;
    private readonly ISessionHubConnection _hub;
    private readonly TimeProvider _clock;
    private readonly SessionSettings _settings;
    private readonly ILogger<SessionHeartbeatService> _log;
    private readonly object _gate = new();

    private ITimer? _timer;
    private Guid? _sessionId;

    public SessionHeartbeatService(
        SessionCoordinator coordinator,
        ISessionHubConnection hub,
        IOptions<SessionSettings> settings,
        TimeProvider? clock = null,
        ILogger<SessionHeartbeatService>? log = null)
    {
        _coordinator = coordinator;
        _hub = hub;
        _clock = clock ?? TimeProvider.System;
        _settings = settings.Value;
        _log = log ?? NullLogger<SessionHeartbeatService>.Instance;

        _coordinator.SessionJoined += OnSessionJoined;
        _coordinator.SessionLeft += OnSessionLeft;
    }

    public Guid? ActiveSessionId
    {
        get { lock (_gate) return _sessionId; }
    }

    private void OnSessionJoined(object? sender, FocusAgent.Core.Dtos.SessionStartedPayload payload)
    {
        StartFor(payload.SessionId);
    }

    private void OnSessionLeft(object? sender, Guid sessionId)
    {
        Stop(sessionId);
    }

    internal void StartFor(Guid sessionId)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _settings.HeartbeatIntervalSeconds));
        ITimer? toDispose = null;
        lock (_gate)
        {
            toDispose = _timer;
            _sessionId = sessionId;
            _timer = _clock.CreateTimer(
                _ => OnTimerFired(sessionId),
                state: null,
                dueTime: interval,
                period: interval);
        }
        toDispose?.Dispose();
        _log.LogInformation(
            "SessionHeartbeatService started for session {SessionId} (interval={IntervalSeconds}s)",
            sessionId, (int)interval.TotalSeconds);
    }

    internal void Stop(Guid sessionId)
    {
        ITimer? toDispose;
        lock (_gate)
        {
            if (_sessionId != sessionId) return;
            toDispose = _timer;
            _timer = null;
            _sessionId = null;
        }
        toDispose?.Dispose();
        _log.LogInformation("SessionHeartbeatService stopped for session {SessionId}", sessionId);
    }

    private void OnTimerFired(Guid sessionId)
    {
        // The timer callback is sync but the hub call is async. Discarding the
        // task is intentional: a failed ping is non-fatal (the backend treats
        // absence-of-heartbeat as the signal), and SignalR's reconnect handles
        // the underlying transport. Log at debug only.
        _ = SafePingAsync(sessionId);
    }

    private async Task SafePingAsync(Guid sessionId)
    {
        // Cheap re-check under the gate to avoid pinging right after Stop()
        // observed but before the timer dispose fully drained a callback in
        // flight.
        lock (_gate)
        {
            if (_sessionId != sessionId) return;
        }

        try
        {
            await _hub.HeartbeatAsync(sessionId).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Heartbeat ping failed for session {SessionId}", sessionId);
        }
    }

    public ValueTask DisposeAsync()
    {
        _coordinator.SessionJoined -= OnSessionJoined;
        _coordinator.SessionLeft -= OnSessionLeft;
        ITimer? toDispose;
        lock (_gate)
        {
            toDispose = _timer;
            _timer = null;
            _sessionId = null;
        }
        toDispose?.Dispose();
        return ValueTask.CompletedTask;
    }
}
