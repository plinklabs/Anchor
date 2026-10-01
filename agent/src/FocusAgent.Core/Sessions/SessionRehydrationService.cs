using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusAgent.Core.Sessions;

/// <summary>
/// Brings the agent's session state in line with the backend each time the
/// hub connection reports <c>Connected</c>.
///
/// Rehydration (#54), once per process: on the first connect after startup,
/// fetches the caller's currently-joined sessions from the backend's
/// rejoinable-sessions endpoint and asks <see cref="SessionCoordinator"/> to
/// silently rejoin each one (no toast — the student already consented before
/// the crash that took the previous process down).
///   * A successful pass sets a latch, so later connects don't re-fetch or
///     rejoin: a student who left a session while offline stays out of it.
///   * If the pass throws (e.g. network failure between hub Connected and the
///     REST call), the latch stays unset so the next connect tries again.
///
/// Then, on every connect, it has the coordinator ask the backend whether the
/// student is still in their joined session
/// (<see cref="SessionCoordinator.ConfirmJoinedSessionAsync"/>, #354), and for a
/// session that started without the agent hearing it
/// (<see cref="SessionCoordinator.CatchUpStartedSessionAsync"/>, #356). Every
/// reconnect is a new SignalR connection, and whatever the backend broadcast
/// while the agent was offline — <c>SessionEnded</c>, <c>SessionStarted</c> —
/// never reaches it. Nor does what it broadcasts in the moment after the
/// connection opens, before the backend has added it to the student's user
/// group. Both questions are hub calls, which the backend answers only once it
/// has.
///
/// The trigger is driven externally via <see cref="NotifyConnectedAsync"/>
/// rather than by subscribing to <c>ConnectionManager</c> directly, because
/// that class lives in the App layer (it owns WAM and the SignalR transport).
/// Keeping this in Core lets it be unit-tested without WinUI on the stack.
/// </summary>
public sealed class SessionRehydrationService
{
    private readonly ISessionRehydrationClient _client;
    private readonly SessionCoordinator _coordinator;
    private readonly ILogger<SessionRehydrationService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private bool _completed;

    public SessionRehydrationService(
        ISessionRehydrationClient client,
        SessionCoordinator coordinator,
        ILogger<SessionRehydrationService>? log = null)
    {
        _client = client;
        _coordinator = coordinator;
        _log = log ?? NullLogger<SessionRehydrationService>.Instance;
    }

    /// <summary>True once a rehydration pass completed without throwing.</summary>
    public bool HasRehydrated
    {
        get
        {
            _gate.Wait();
            try { return _completed; }
            finally { _gate.Release(); }
        }
    }

    /// <summary>
    /// Hook called by the App layer each time its connection manager
    /// transitions to Connected. Async-void-safe: exceptions are caught and
    /// logged.
    /// </summary>
    public async Task NotifyConnectedAsync(CancellationToken ct = default)
    {
        // Overlapping triggers wait their turn rather than being dropped: the
        // gate keeps two /sessions/rejoinable fetches from running at once, and
        // a reconnect that lands while an earlier pass runs still gets its own
        // check — the earlier one may have asked over the connection that just
        // dropped.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_completed)
            {
                try
                {
                    await RehydrateAsync(ct).ConfigureAwait(false);
                    _completed = true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex,
                        "Session rehydration failed; the student's previous session won't be re-attached until the teacher starts a new one or the agent is restarted.");
                    // Leave _completed false so the next NotifyConnectedAsync retries.
                }
            }

            // Never throws but for cancellation: a failed check keeps the session.
            await _coordinator.ConfirmJoinedSessionAsync(ct).ConfigureAwait(false);

            // After the confirm, so a session that ended while the agent was
            // offline is left before a newer one is offered; after rehydration,
            // so a session the student was already in is rejoined silently
            // rather than asked about again. Never throws but for cancellation.
            await _coordinator.CatchUpStartedSessionAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RehydrateAsync(CancellationToken ct)
    {
        var sessions = await _client.GetRejoinableSessionsAsync(ct).ConfigureAwait(false);
        if (sessions.Count == 0)
        {
            _log.LogDebug("No rejoinable sessions for this student.");
            return;
        }

        _log.LogInformation(
            "Rehydration: backend reports {Count} rejoinable session(s); attempting silent rejoin.",
            sessions.Count);

        foreach (var payload in sessions)
        {
            if (ct.IsCancellationRequested) return;
            // SessionCoordinator.RejoinAsync is itself idempotent on
            // already-joined sessions, so even if a SessionStarted broadcast
            // raced us we won't double-join.
            await _coordinator.RejoinAsync(payload, ct).ConfigureAwait(false);
        }
    }
}
