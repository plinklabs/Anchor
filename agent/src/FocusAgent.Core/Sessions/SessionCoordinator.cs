using FocusAgent.Core.Dtos;
using FocusAgent.Core.Realtime;
using FocusAgent.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FocusAgent.Core.Sessions;

public sealed class SessionCoordinator : IAsyncDisposable
{
    // Must parse (case-insensitive) to Anchor.Domain.Events.EventKind.ManualLeave
    // on the backend's ReportEvent. Mirrors SignalRFocusEventReporter's kind
    // strings — the agent doesn't reference the backend enum.
    private const string ManualLeaveKind = "ManualLeave";

    // Parses (case-insensitive) to Anchor.Domain.Events.EventKind.AgentKilled on
    // the backend's ReportEvent (#110).
    private const string AgentKilledKind = "AgentKilled";

    private readonly ISessionHubConnection _hub;
    private readonly ISessionUiHost _ui;
    private readonly TimeProvider _clock;
    private readonly RealtimeSettings _settings;
    private readonly ILogger<SessionCoordinator> _log;
    private readonly object _gate = new();

    private JoinConfirmation? _active;
    private Guid? _activeSessionId;
    private Guid? _joinedSessionId;

    public SessionCoordinator(
        ISessionHubConnection hub,
        ISessionUiHost ui,
        IOptions<RealtimeSettings> settings,
        TimeProvider? clock = null,
        ILogger<SessionCoordinator>? log = null)
    {
        _hub = hub;
        _ui = ui;
        _clock = clock ?? TimeProvider.System;
        _settings = settings.Value;
        _log = log ?? NullLogger<SessionCoordinator>.Instance;

        _hub.SessionStarted += OnSessionStarted;
        _hub.SessionEnded += OnSessionEnded;
        _hub.SessionBundlesUpdated += OnSessionBundlesUpdated;
    }

    public JoinConfirmation? ActiveConfirmation
    {
        get { lock (_gate) return _active; }
    }

    public Guid? ActiveSessionId
    {
        get { lock (_gate) return _activeSessionId; }
    }

    public Guid? JoinedSessionId
    {
        get { lock (_gate) return _joinedSessionId; }
    }

    /// <summary>
    /// Fires after the student successfully joins a session (post-confirmation,
    /// after <c>JoinSession</c> returns). Subscribers should treat this as the
    /// signal that focus enforcement should begin.
    /// </summary>
    public event EventHandler<SessionStartedPayload>? SessionJoined;

    /// <summary>
    /// Fires when an active session ends — teacher action, decline-after-join,
    /// or local cleanup. Subscribers should stop any in-session work.
    /// </summary>
    public event EventHandler<Guid>? SessionLeft;

    /// <summary>
    /// Fires when the teacher changes the bundles of the currently-joined
    /// session (#93). Carries the full recomputed allowlist; subscribers should
    /// replace their enforcement rules. Only raised for the joined session —
    /// updates for any other session are dropped here.
    /// </summary>
    public event EventHandler<SessionBundlesUpdatedPayload>? SessionAllowlistUpdated;

    private async void OnSessionStarted(object? sender, SessionStartedPayload payload)
    {
        try
        {
            await HandleSessionStartedAsync(payload).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed handling SessionStarted for {SessionId}", payload.SessionId);
        }
    }

    internal async Task HandleSessionStartedAsync(SessionStartedPayload payload, CancellationToken ct = default)
    {
        // Already joined this session (e.g. the rehydration service rejoined
        // it just before the teacher's SessionStarted broadcast arrived).
        // Don't re-prompt or re-call JoinSession — the agent is already in
        // the correct state. Matches the #54 race-double-join guard.
        lock (_gate)
        {
            if (_joinedSessionId == payload.SessionId)
            {
                _log.LogInformation(
                    "SessionStarted received for already-joined session {SessionId}; skipping toast.",
                    payload.SessionId);
                return;
            }
        }

        // SessionStartedPayload does not yet carry the teacher's display name.
        // Until the dashboard-polish issue lands it, surface a generic label.
        const string teacherPlaceholder = "Your teacher";

        var confirmation = new JoinConfirmation(payload, teacherPlaceholder, _settings.JoinConfirmationDuration, _clock);

        lock (_gate)
        {
            _active?.Abort();
            _active = confirmation;
            _activeSessionId = payload.SessionId;
        }

        _log.LogInformation("Session {SessionId} started; awaiting student confirmation", payload.SessionId);

        var decision = await _ui.ShowJoinConfirmationAsync(confirmation, ct).ConfigureAwait(false);

        var joined = false;
        switch (decision)
        {
            case JoinDecision.Confirmed:
                try
                {
                    await _hub.JoinSessionAsync(payload.SessionId, joinCode: null, ct).ConfigureAwait(false);
                    joined = true;
                    _log.LogInformation("Joined session {SessionId}", payload.SessionId);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "JoinSession failed for {SessionId}", payload.SessionId);
                }
                break;
            case JoinDecision.Declined:
                try
                {
                    await _hub.DeclineSessionAsync(payload.SessionId, "user_cancelled", ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "DeclineSession failed for {SessionId}", payload.SessionId);
                }
                _log.LogInformation("Declined session {SessionId}", payload.SessionId);
                break;
            case JoinDecision.Aborted:
                _log.LogInformation("Session {SessionId} confirmation aborted", payload.SessionId);
                break;
        }

        lock (_gate)
        {
            if (ReferenceEquals(_active, confirmation))
            {
                _active = null;
                if (decision != JoinDecision.Confirmed)
                    _activeSessionId = null;
            }
            if (joined)
                _joinedSessionId = payload.SessionId;
        }

        if (joined)
            SessionJoined?.Invoke(this, payload);
    }

    /// <summary>
    /// Re-attaches to an already-running session without going through the
    /// 5-second join-confirmation toast. Used after an agent restart (#54)
    /// when <c>SessionRehydrationService</c> has discovered, via the
    /// <c>/sessions/rejoinable</c> endpoint, that the student is still an
    /// active participant of <paramref name="payload"/>. The student already
    /// consented to this session before the crash; re-prompting would just
    /// annoy them.
    ///
    /// No-op if already joined to the same session, so a race between
    /// rehydration and a fresh <c>SessionStarted</c> broadcast can't double-join.
    /// </summary>
    public async Task RejoinAsync(SessionStartedPayload payload, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_joinedSessionId == payload.SessionId)
            {
                _log.LogDebug("RejoinAsync no-op — already joined {SessionId}.", payload.SessionId);
                return;
            }
        }

        try
        {
            await _hub.JoinSessionAsync(payload.SessionId, joinCode: null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "RejoinAsync: JoinSession failed for {SessionId}", payload.SessionId);
            return;
        }

        bool fire;
        lock (_gate)
        {
            // Double-check under the gate in case another path joined between
            // our pre-check and the hub call returning.
            if (_joinedSessionId == payload.SessionId)
            {
                fire = false;
            }
            else
            {
                _joinedSessionId = payload.SessionId;
                fire = true;
            }
        }

        if (fire)
        {
            _log.LogInformation("Rejoined session {SessionId} after agent restart.", payload.SessionId);
            SessionJoined?.Invoke(this, payload);
        }
    }

    /// <summary>
    /// The student chose to leave the current session from the agent UI (#102).
    /// Records a <c>ManualLeave</c> event for the teacher's post-session review,
    /// tells the backend the participant left (which broadcasts a "Left" state to
    /// the teacher's live roster), then ends the session locally so focus
    /// enforcement and the heartbeat stop. The agent itself keeps running.
    ///
    /// No-op when not currently in a session.
    /// </summary>
    public async Task LeaveSessionManuallyAsync(CancellationToken ct = default)
    {
        Guid sessionId;
        lock (_gate)
        {
            if (_joinedSessionId is not Guid joined)
            {
                _log.LogDebug("LeaveSessionManuallyAsync no-op — not in a session.");
                return;
            }
            sessionId = joined;
        }

        // Report the event *before* leaving: the hub's ReportEvent rejects an
        // event from a participant whose LeftAt is set, and LeaveSession is what
        // sets it. Best-effort — a failed report must not strand the student in
        // a session they asked to leave, so we still tear down locally below.
        try
        {
            await _hub.ReportEventAsync(sessionId, ManualLeaveKind, payloadJson: "{}", _clock.GetUtcNow(), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reporting ManualLeave failed for {SessionId}", sessionId);
        }

        try
        {
            await _hub.LeaveSessionAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "LeaveSession failed for {SessionId}", sessionId);
        }

        bool fire;
        lock (_gate)
        {
            // Only this path's session should be torn down; a concurrent
            // SessionEnded may have already cleared it.
            fire = _joinedSessionId == sessionId;
            if (_joinedSessionId == sessionId) _joinedSessionId = null;
            if (_activeSessionId == sessionId) _activeSessionId = null;
        }

        if (fire)
        {
            _log.LogInformation("Student manually left session {SessionId}", sessionId);
            SessionLeft?.Invoke(this, sessionId);
        }
    }

    /// <summary>
    /// The student is deliberately quitting the agent while in a session (#110).
    /// Best-effort post of an <c>AgentKilled</c> event so the teacher's live
    /// roster shows the departure at once instead of waiting out the
    /// <c>HeartbeatLost</c> timeout — the agent already knows the answer at the
    /// moment of Quit.
    ///
    /// Unlike <see cref="LeaveSessionManuallyAsync"/> this does NOT call
    /// <c>LeaveSession</c> or clear local state: the process is on its way out, so
    /// there is nothing left to tear down, and the backend marks the participant
    /// left off the <c>AgentKilled</c> event itself. No-op outside a session.
    /// Errors are swallowed — a flaky network must never delay or block Quit; the
    /// caller additionally time-boxes the wait.
    /// </summary>
    public async Task ReportAgentKilledAsync(CancellationToken ct = default)
    {
        Guid sessionId;
        lock (_gate)
        {
            if (_joinedSessionId is not Guid joined)
            {
                _log.LogDebug("ReportAgentKilledAsync no-op — not in a session.");
                return;
            }
            sessionId = joined;
        }

        try
        {
            await _hub.ReportEventAsync(sessionId, AgentKilledKind, payloadJson: "{}", _clock.GetUtcNow(), ct)
                .ConfigureAwait(false);
            _log.LogInformation("Reported AgentKilled for session {SessionId} on quit", sessionId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reporting AgentKilled failed for {SessionId}", sessionId);
        }
    }

    private void OnSessionBundlesUpdated(object? sender, SessionBundlesUpdatedPayload payload)
    {
        bool forJoinedSession;
        lock (_gate)
        {
            forJoinedSession = _joinedSessionId == payload.SessionId;
        }

        if (!forJoinedSession)
        {
            _log.LogDebug(
                "Ignoring SessionBundlesUpdated for {SessionId} — not the joined session.",
                payload.SessionId);
            return;
        }

        _log.LogInformation("Allowlist updated for joined session {SessionId}", payload.SessionId);
        SessionAllowlistUpdated?.Invoke(this, payload);
    }

    private void OnSessionEnded(object? sender, Guid sessionId)
    {
        try
        {
            HandleSessionEnded(sessionId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed handling SessionEnded for {SessionId}", sessionId);
        }
    }

    internal void HandleSessionEnded(Guid sessionId)
    {
        JoinConfirmation? toCancel = null;
        bool matched;
        bool wasJoined;
        lock (_gate)
        {
            if (_active is not null && _active.Payload.SessionId == sessionId)
            {
                toCancel = _active;
                _active = null;
            }
            matched = _activeSessionId == sessionId;
            if (matched)
                _activeSessionId = null;
            wasJoined = _joinedSessionId == sessionId;
            if (wasJoined)
                _joinedSessionId = null;
        }
        toCancel?.Abort();
        if (matched)
            _ui.DismissJoinConfirmation();
        if (wasJoined)
            SessionLeft?.Invoke(this, sessionId);
        _log.LogInformation("Session {SessionId} ended", sessionId);
    }

    public async ValueTask DisposeAsync()
    {
        _hub.SessionStarted -= OnSessionStarted;
        _hub.SessionEnded -= OnSessionEnded;
        _hub.SessionBundlesUpdated -= OnSessionBundlesUpdated;
        JoinConfirmation? active;
        Guid? joined;
        lock (_gate)
        {
            active = _active;
            _active = null;
            joined = _joinedSessionId;
            _joinedSessionId = null;
        }
        active?.Abort();
        if (joined is Guid id)
            SessionLeft?.Invoke(this, id);
        await Task.CompletedTask;
    }
}
