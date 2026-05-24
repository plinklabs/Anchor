using FocusAgent.Core.Auth;
using FocusAgent.Core.Dtos;
using FocusAgent.Core.Realtime;
using FocusAgent.Core.Settings;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FocusAgent.App.Realtime;

public sealed class SignalRSessionHubConnection : ISessionHubConnection
{
    private readonly HubConnection _connection;
    private readonly RealtimeSettings _realtime;
    private readonly ILogger<SignalRSessionHubConnection> _log;
    private readonly object _gate = new();

    private AgentConnectionState _state = AgentConnectionState.SignedOut;

    public SignalRSessionHubConnection(
        IOptions<BackendSettings> backend,
        IOptions<RealtimeSettings> realtime,
        IOptions<DevSettings> dev,
        IAuthTokenProvider tokens,
        ILogger<SignalRSessionHubConnection> log)
    {
        _realtime = realtime.Value;
        _log = log;

        var backendValue = backend.Value;
        var baseUrl = backendValue.BaseUrl.TrimEnd('/');
        var hubPath = backendValue.HubPath.StartsWith('/') ? backendValue.HubPath : "/" + backendValue.HubPath;
        var hubUrl = baseUrl + hubPath;

        var impersonateOid = dev.Value.ImpersonateOid?.Trim();

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = async () => await tokens.GetAccessTokenAsync().ConfigureAwait(false);
                if (!string.IsNullOrEmpty(impersonateOid))
                    options.Headers["X-Dev-Impersonate-Oid"] = impersonateOid;
            })
            .WithAutomaticReconnect(new SignalRBackoffPolicy(_realtime.ReconnectMaxBackoff))
            .Build();

        if (!string.IsNullOrEmpty(impersonateOid))
            _log.LogWarning("Dev impersonation enabled — hub will resolve user as OID {Oid}", impersonateOid);

        _connection.On<SessionStartedPayload>("SessionStarted", payload =>
        {
            _log.LogInformation(
                "Hub received SessionStarted for session {SessionId} (class {ClassId}, mode {Mode})",
                payload.SessionId, payload.ClassId, payload.Mode);
            SessionStarted?.Invoke(this, payload);
        });
        _connection.On<Guid>("SessionEnded", sessionId =>
        {
            _log.LogInformation("Hub received SessionEnded for session {SessionId}", sessionId);
            SessionEnded?.Invoke(this, sessionId);
        });

        _connection.Reconnecting += _ =>
        {
            SetState(AgentConnectionState.Reconnecting);
            return Task.CompletedTask;
        };
        _connection.Reconnected += _ =>
        {
            SetState(AgentConnectionState.Connected);
            return Task.CompletedTask;
        };
        _connection.Closed += _ =>
        {
            SetState(AgentConnectionState.Disconnected);
            return Task.CompletedTask;
        };
    }

    public AgentConnectionState State
    {
        get { lock (_gate) return _state; }
    }

    public event EventHandler<AgentConnectionState>? StateChanged;
    public event EventHandler<SessionStartedPayload>? SessionStarted;
    public event EventHandler<Guid>? SessionEnded;

    public async Task StartAsync(CancellationToken ct = default)
    {
        SetState(AgentConnectionState.Connecting);
        try
        {
            await _connection.StartAsync(ct).ConfigureAwait(false);
            SetState(AgentConnectionState.Connected);
        }
        catch
        {
            SetState(AgentConnectionState.Disconnected);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _connection.StopAsync(ct).ConfigureAwait(false);
        SetState(AgentConnectionState.Disconnected);
    }

    public Task JoinSessionAsync(Guid sessionId, string? joinCode, CancellationToken ct = default) =>
        _connection.InvokeAsync(
            "JoinSession",
            new JoinSessionRequest(sessionId, joinCode),
            ct);

    public Task LeaveSessionAsync(Guid sessionId, CancellationToken ct = default) =>
        _connection.InvokeAsync("LeaveSession", sessionId, ct);

    public Task DeclineSessionAsync(Guid sessionId, string reason, CancellationToken ct = default) =>
        _connection.InvokeAsync(
            "DeclineSession",
            new DeclineSessionRequest(sessionId, reason),
            ct);

    public Task ReportEventAsync(Guid sessionId, string kind, string payloadJson, DateTimeOffset? occurredAt = null, CancellationToken ct = default) =>
        _connection.InvokeAsync(
            "ReportEvent",
            new ReportEventRequest(sessionId, kind, payloadJson, occurredAt),
            ct);

    public Task HeartbeatAsync(Guid sessionId, CancellationToken ct = default)
    {
        if (_connection.State != HubConnectionState.Connected)
            return Task.CompletedTask;
        return _connection.InvokeAsync("Heartbeat", sessionId, ct);
    }

    private void SetState(AgentConnectionState next)
    {
        bool changed;
        lock (_gate)
        {
            changed = _state != next;
            _state = next;
        }
        if (changed)
        {
            _log.LogDebug("Hub connection state -> {State}", next);
            StateChanged?.Invoke(this, next);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private sealed record JoinSessionRequest(Guid SessionId, string? JoinCode);
    private sealed record ReportEventRequest(Guid SessionId, string Kind, string? PayloadJson, DateTimeOffset? OccurredAt);
    private sealed record DeclineSessionRequest(Guid SessionId, string? Reason);
}
