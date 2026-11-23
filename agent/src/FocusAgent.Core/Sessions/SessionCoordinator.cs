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

    // #570: the joined session's allowlist as this agent last handed it out — the
    // join, a bundle change, an approved request — so a reconnect's catch-up
    // replaces it only when the backend's differs.
    private SessionAllowlistPayload? _allowlist;

    // #570: bumped by every allowlist the hub pushes, so a catch-up answer that a
    // newer push overtook is not applied over it.
    private long _allowlistVersion;

    // Sessions whose start this agent has handled — from the SessionStarted
    // broadcast, a rejoin, or a catch-up — so a catch-up after a reconnect
    // (#356) never replays one the student has already answered.
    private readonly HashSet<Guid> _handledStarts = new();

    // #456: rejoins whose hub call is still out. Between the pre-check and the
    // call returning, nothing else shows the join, yet it is one.
    private int _joinsInFlight;

    // #456: set while the agent restarts into an update. Session starts and
    // rejoins are left for the next process, whose catch-up (#356) and
    // rehydration (#54) pick them up.
    private bool _closingForRestart;

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
        _hub.AllowlistAmended += OnAllowlistAmended;
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
    /// True once <see cref="TryCloseForRestart"/> has succeeded (#456): the agent is
    /// on its way out to restart into an update and takes no more sessions.
    /// </summary>
    public bool IsClosingForRestart
    {
        get { lock (_gate) return _closingForRestart; }
    }

    /// <summary>
    /// True while the agent has a session going on: one it is in, one it is asking
    /// the student about (the join confirmation), or a join on its way (a rejoin
    /// after a restart whose hub call is still out). The same notion that holds back
    /// the restart into an update (<see cref="TryCloseForRestart"/>, #456); the tray
    /// offers no "Exit Anchor" while it holds (#445), so the student leaves the
    /// session from the main window first.
    /// </summary>
    public bool HasSessionGoingOn
    {
        get { lock (_gate) return SessionGoingOn(); }
    }

    /// <summary>
    /// Stop taking sessions so the agent can restart into an update (#456), but only
    /// if it has no session going on (<see cref="HasSessionGoingOn"/>): none it is
    /// in, none it is asking the student about, and no join on its way. The check
    /// and the stop are one step under the same lock as every session change, so no
    /// session can begin in between; from then on a session start (broadcast or
    /// catch-up) and a rejoin are ignored and left to the restarted agent. Returns
    /// false, and changes nothing, when a session is going on.
    /// </summary>
    public bool TryCloseForRestart()
    {
        lock (_gate)
        {
            if (_closingForRestart) return true;
            if (SessionGoingOn())
                return false;
            _closingForRestart = true;
            return true;
        }
    }

    // Call under _gate.
    private bool SessionGoingOn() =>
        _active is not null || _activeSessionId is not null || _joinedSessionId is not null || _joinsInFlight > 0;

    /// <summary>
    /// Take sessions again after <see cref="TryCloseForRestart"/>, when the restart
    /// could not be handed over and the agent keeps running (#456).
    /// </summary>
    public void ReopenAfterAbortedRestart()
    {
        lock (_gate) _closingForRestart = false;
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
    /// session (#93), and when a reconnect finds the student's allowlist changed
    /// while the agent was offline (#570, <see cref="RefreshJoinedAllowlistAsync"/>).
    /// Carries the full recomputed allowlist; subscribers should replace their
    /// enforcement rules. Only raised for the joined session — updates for any
    /// other session are dropped here.
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

    internal Task HandleSessionStartedAsync(SessionStartedPayload payload, CancellationToken ct = default) =>
        HandleSessionStartedAsync(payload, catchingUp: false, ct);

    private async Task HandleSessionStartedAsync(SessionStartedPayload payload, bool catchingUp, CancellationToken ct)
    {
        // SessionStartedPayload does not yet carry the teacher's display name.
        // Until the dashboard-polish issue lands it, surface a generic label.
        const string teacherPlaceholder = "Your teacher";

        JoinConfirmation confirmation;
        lock (_gate)
        {
            // #456: the agent is restarting into an update. Not recorded as
            // handled, so a catch-up can still offer it should the restart be
            // called off; normally the restarted agent's catch-up does.
            if (_closingForRestart)
            {
                _log.LogInformation(
                    "Session {SessionId} started while the agent is restarting into an update; the restarted agent picks it up.",
                    payload.SessionId);
                return;
            }

            // A catch-up (#356) stands in only for a SessionStarted this agent
            // never handled. One it did handle it is in, is asking about, or
            // the student already answered.
            if (!_handledStarts.Add(payload.SessionId) && catchingUp)
                return;

            // Already joined this session (e.g. the rehydration service rejoined
            // it just before the teacher's SessionStarted broadcast arrived).
            // Don't re-prompt or re-call JoinSession — the agent is already in
            // the correct state. Matches the #54 race-double-join guard.
            if (_joinedSessionId == payload.SessionId)
            {
                _log.LogInformation(
                    "SessionStarted received for already-joined session {SessionId}; skipping toast.",
                    payload.SessionId);
                return;
            }

            // The student is being asked about it right now. A start can arrive
            // twice: by catch-up and by broadcast, when the session began just
            // as the agent connected.
            if (_active?.Payload.SessionId == payload.SessionId)
            {
                _log.LogInformation(
                    "SessionStarted received for session {SessionId}, whose join confirmation is already showing; skipping.",
                    payload.SessionId);
                return;
            }

            confirmation = new JoinConfirmation(payload, teacherPlaceholder, _settings.JoinConfirmationDuration, _clock);
            _active?.Abort();
            _active = confirmation;
            _activeSessionId = payload.SessionId;
        }

        if (catchingUp)
            _log.LogInformation("Session {SessionId} started while the agent wasn't listening; catching up.", payload.SessionId);
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
                SetJoined(payload);
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
            // #456: the restarted agent's rehydration rejoins it instead.
            if (_closingForRestart)
            {
                _log.LogInformation(
                    "Not rejoining session {SessionId}: the agent is restarting into an update.", payload.SessionId);
                return;
            }
            _joinsInFlight++;
        }

        try
        {
            await _hub.JoinSessionAsync(payload.SessionId, joinCode: null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_gate) _joinsInFlight--;
            _log.LogWarning(ex, "RejoinAsync: JoinSession failed for {SessionId}", payload.SessionId);
            return;
        }

        bool fire;
        lock (_gate)
        {
            _joinsInFlight--;
            _handledStarts.Add(payload.SessionId);
            // Double-check under the gate in case another path joined between
            // our pre-check and the hub call returning.
            if (_joinedSessionId == payload.SessionId)
            {
                fire = false;
            }
            else
            {
                SetJoined(payload);
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
    /// Asks the backend whether the student is still in the joined session, and
    /// ends it locally if not (#354). Called each time the hub connection comes
    /// back: <c>SessionEnded</c> reaches only the connections open when it is
    /// sent, so a session that ended while the agent was offline — a network
    /// drop, a backend restart, a laptop asleep through the automatic end of a
    /// forgotten session — would otherwise keep the student in focus mode.
    ///
    /// Only a "no" from the backend ends the session. If the question fails (the
    /// connection dropped again, a backend without the check) the agent stays in
    /// the session and asks again on the next reconnect. No-op when not joined.
    /// </summary>
    public async Task ConfirmJoinedSessionAsync(CancellationToken ct = default)
    {
        Guid sessionId;
        lock (_gate)
        {
            if (_joinedSessionId is not Guid joined)
                return;
            sessionId = joined;
        }

        bool stillIn;
        try
        {
            stillIn = await _hub.IsInSessionAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not confirm session {SessionId} with the backend; staying in it.", sessionId);
            return;
        }

        if (stillIn)
            return;

        _log.LogInformation(
            "Session {SessionId} ended or was left while the agent was disconnected; leaving it.", sessionId);
        HandleSessionEnded(sessionId);
    }

    /// <summary>
    /// Asks the backend for the student's allowlist in the joined session and,
    /// when it differs from the one the agent handed out, replaces it: raises
    /// <see cref="SessionAllowlistUpdated"/>, which the focus controller (apps)
    /// and the extension relay (<c>allowlist_replaced</c>) take as a bundle
    /// change (#570). Called each time the hub connection comes back, after
    /// <see cref="ConfirmJoinedSessionAsync"/>: what the teacher changes on the
    /// student's allowlist — a request approved, a bundle added or removed —
    /// reaches only the connections open when it is sent, so a change made
    /// while the agent was offline would otherwise never arrive.
    ///
    /// Nothing is raised when the allowlist is unchanged, so a reconnect does
    /// not have the extension recheck every tab for nothing; nor when a push
    /// arrived while the question was out (it is newer), when the backend says
    /// the student is not in the session (<see cref="ConfirmJoinedSessionAsync"/>
    /// decides that), or when the question fails — a backend too old to know it
    /// included. No-op when not joined. Never throws but for cancellation.
    /// </summary>
    public async Task RefreshJoinedAllowlistAsync(CancellationToken ct = default)
    {
        Guid sessionId;
        long version;
        lock (_gate)
        {
            if (_joinedSessionId is not Guid joined)
                return;
            sessionId = joined;
            version = _allowlistVersion;
        }

        SessionAllowlistPayload? current;
        try
        {
            current = await _hub.GetSessionAllowlistAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not ask the backend for the allowlist of session {SessionId}; keeping the one the agent has.", sessionId);
            return;
        }

        if (current is null || current.SessionId != sessionId)
            return;

        lock (_gate)
        {
            if (_joinedSessionId != sessionId || _allowlistVersion != version)
            {
                _log.LogDebug("Allowlist of session {SessionId} changed while the agent asked for it; keeping the newer one.", sessionId);
                return;
            }
            if (_allowlist is { } held && SameAllowlist(held, current))
            {
                _log.LogDebug("Allowlist of session {SessionId} unchanged after reconnecting.", sessionId);
                return;
            }
            _allowlist = current;
            _allowlistVersion++;
        }

        _log.LogInformation(
            "Allowlist of session {SessionId} changed while the agent was disconnected ({AppCount} apps, {DomainCount} domains); replacing it.",
            sessionId, current.Apps.Count, current.Domains.Count);
        SessionAllowlistUpdated?.Invoke(this, new SessionBundlesUpdatedPayload(sessionId, current.Apps, current.Domains));
    }

    /// <summary>
    /// Asks the backend for the running session the student has been asked into
    /// and, if this agent never heard that session start, handles it as the
    /// <c>SessionStarted</c> it missed: the join confirmation, then the join
    /// (#356). Called each time the hub connection comes up, after
    /// <see cref="ConfirmJoinedSessionAsync"/>. The broadcast reaches only the
    /// connections in the student's user group when it is sent, which misses an
    /// agent that was offline when the teacher started the session — and one
    /// that had only just connected, because the hub connection reports
    /// connected before the backend has added it to that group. The question
    /// is answered only once the backend has, so between the two the agent
    /// misses no start.
    ///
    /// Waits for the student's answer, as a broadcast start does. Does nothing
    /// for a session the agent is in, is asking about, or already handled — so
    /// it never re-asks one the student declined or left — and nothing when the
    /// question fails: there is no session to start. Never throws but for
    /// cancellation.
    /// </summary>
    public async Task CatchUpStartedSessionAsync(CancellationToken ct = default)
    {
        SessionStartedPayload? started;
        try
        {
            started = await _hub.GetStartedSessionAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not ask the backend for a session that started while the agent wasn't listening.");
            return;
        }

        if (started is null)
            return;

        try
        {
            await HandleSessionStartedAsync(started, catchingUp: true, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed catching up on session {SessionId}", started.SessionId);
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
            if (forJoinedSession)
            {
                _allowlist = new SessionAllowlistPayload(payload.SessionId, payload.Apps, payload.Domains);
                _allowlistVersion++;
            }
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

    // The extension relay hands an approval to the extension; here it only
    // joins the allowlist held for the reconnect catch-up (#570), which would
    // otherwise find the approved host new and replace the list for nothing.
    private void OnAllowlistAmended(object? sender, AllowlistAmendedPayload payload)
    {
        lock (_gate)
        {
            if (_joinedSessionId != payload.SessionId || _allowlist is not { } held)
                return;
            var domains = held.Domains.ToList();
            foreach (var domain in payload.AddedDomains)
            {
                if (!string.IsNullOrWhiteSpace(domain.Value) && !domains.Contains(domain, AllowlistEntryComparer.Domains))
                    domains.Add(domain);
            }
            _allowlist = held with { Domains = domains };
            _allowlistVersion++;
        }
    }

    // Call under _gate.
    private void SetJoined(SessionStartedPayload payload)
    {
        _joinedSessionId = payload.SessionId;
        _allowlist = new SessionAllowlistPayload(payload.SessionId, payload.Apps, payload.Domains);
        _allowlistVersion++;
    }

    // The same entries in any order: the backend builds the list afresh on each
    // answer, and an approval appends to the one the agent holds.
    private static bool SameAllowlist(SessionAllowlistPayload a, SessionAllowlistPayload b) =>
        a.Apps.ToHashSet(AllowlistEntryComparer.Apps).SetEquals(b.Apps) &&
        a.Domains.ToHashSet(AllowlistEntryComparer.Domains).SetEquals(b.Domains);

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
        _hub.AllowlistAmended -= OnAllowlistAmended;
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

/// <summary>
/// Allowlist entries compared as the backend dedupes them (#570): the same match
/// kind and value, the value in any case.
/// </summary>
internal static class AllowlistEntryComparer
{
    public static readonly IEqualityComparer<AllowedAppDto> Apps = new Comparer<AllowedAppDto>(a => (a.MatchKind, a.Value));
    public static readonly IEqualityComparer<AllowedDomainDto> Domains = new Comparer<AllowedDomainDto>(d => (d.MatchType, d.Value));

    private sealed class Comparer<T>(Func<T, (string Kind, string Value)> key) : IEqualityComparer<T>
    {
        public bool Equals(T? x, T? y)
        {
            if (x is null || y is null) return x is null && y is null;
            var (kx, vx) = key(x);
            var (ky, vy) = key(y);
            return string.Equals(kx, ky, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(vx, vy, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode(T obj)
        {
            var (kind, value) = key(obj);
            return HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(kind ?? ""),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value ?? ""));
        }
    }
}
