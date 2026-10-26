using Anchor.Api.Sessions;
using Anchor.Domain.Events;
using Anchor.Domain.Sessions;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Anchor.Api.Realtime;

[Authorize]
public sealed class SessionHub : Hub<ISessionHubClient>
{
    public const string Path = "/hubs/session";

    /// <summary>
    /// Dev-only header that overrides the resolved user OID with the given
    /// value, letting one machine run the agent as a seeded student while the
    /// dashboard runs as the signed-in teacher. Honored only when the host
    /// environment is Development. See issue #38.
    /// </summary>
    public const string DevImpersonateOidHeader = "X-Dev-Impersonate-Oid";

    private const string EntraOidShortClaim = "oid";
    private const string EntraOidLongClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    // The resolved User is fixed for the connection's lifetime (the EntraOid
    // is captured at handshake and can't change without a fresh connection),
    // so we cache it in Context.Items to spare every hub invocation a
    // redundant SELECT Users round-trip. See issue #55.
    private const string ResolvedUserContextItemKey = "anchor.resolved-user";

    private readonly AnchorDbContext _db;
    private readonly IUserStore _users;
    private readonly TimeProvider _clock;
    private readonly IHostEnvironment _env;
    private readonly HeartbeatTracker _heartbeats;
    private readonly ActiveParticipantCache _activeParticipants;
    private readonly ISessionBroadcaster _broadcaster;
    private readonly ISessionAllowlistExpander _allowlist;
    private readonly SessionEndSchedule _endSchedule;
    private readonly ILogger<SessionHub> _log;

    public SessionHub(
        AnchorDbContext db,
        IUserStore users,
        TimeProvider clock,
        IHostEnvironment env,
        HeartbeatTracker heartbeats,
        ActiveParticipantCache activeParticipants,
        ISessionBroadcaster broadcaster,
        ISessionAllowlistExpander allowlist,
        SessionEndSchedule endSchedule,
        ILogger<SessionHub> log)
    {
        _db = db;
        _users = users;
        _clock = clock;
        _env = env;
        _heartbeats = heartbeats;
        _activeParticipants = activeParticipants;
        _broadcaster = broadcaster;
        _allowlist = allowlist;
        _endSchedule = endSchedule;
        _log = log;
    }

    /// <summary>
    /// The session group: the owning teacher's roster feed. Only their
    /// connections join it, through <see cref="JoinSession"/> (#366).
    /// </summary>
    public static string GroupName(Guid sessionId) => $"session:{sessionId:D}";

    public static string UserGroupName(Guid userId) => $"user:{userId:D}";

    public override async Task OnConnectedAsync()
    {
        var ct = Context.ConnectionAborted;
        var user = await TryResolveCurrentUserAsync(ct);
        if (user is not null)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, UserGroupName(user.Id), ct);
            _log.LogInformation(
                "Hub connection {ConnectionId} resolved to user {UserId} ({DisplayName}); joined group {Group}",
                Context.ConnectionId, user.Id, user.DisplayName, UserGroupName(user.Id));
        }
        else
        {
            _log.LogWarning(
                "Hub connection {ConnectionId} could not resolve a user — no user-group subscription. Broadcasts will not reach this connection.",
                Context.ConnectionId);
        }
        await base.OnConnectedAsync();
    }

    public async Task<JoinSessionResult> JoinSession(JoinSessionRequest request)
    {
        var ct = Context.ConnectionAborted;
        var user = await ResolveCurrentUserAsync(ct);

        var session = await _db.Sessions
            .FirstOrDefaultAsync(s => s.Id == request.SessionId, ct);
        if (session is null || session.EndedAt is not null)
            throw new HubException("Session not found or already ended.");

        // The session-owning teacher runs it (they're the TeacherId) and has
        // no participant row unless they chose to take part, by join code
        // (#410). Let them subscribe to their own session without a join code
        // or a participant row.
        var isOwningTeacher = session.TeacherId == user.Id;

        // Past its planned end (#447) the session is over, even in the moment
        // before the auto-end marks it ended: no agent or extension joins or
        // rejoins it any more, so a student whose agent starts after the lesson
        // stays out. Only its teacher's page may still subscribe, to hear it
        // end. Scheduling its end here gets it ended on time, or at once, should
        // the backend have restarted since it started.
        var now = _clock.GetUtcNow();
        if (session.EndsAt is { } endsAt)
            _endSchedule.Schedule(session.Id, endsAt);
        var pastPlannedEnd = Session.IsPastPlannedEnd(session.EndsAt, now);
        if (pastPlannedEnd && !isOwningTeacher)
            throw new HubException("Session not found or already ended.");

        var participant = await _db.SessionParticipants
            .FirstOrDefaultAsync(p => p.SessionId == session.Id && p.UserId == user.Id, ct);

        var joined = false;
        if (participant is null && !isOwningTeacher)
        {
            // Join-by-code fallback: any signed-in user with the correct code
            // may join even without a roster row.
            if (string.IsNullOrWhiteSpace(request.JoinCode) ||
                !string.Equals(request.JoinCode, session.JoinCode, StringComparison.Ordinal))
            {
                throw new HubException("Not a participant of this session.");
            }

            participant = new SessionParticipant
            {
                SessionId = session.Id,
                UserId = user.Id,
                JoinedAt = now,
            };
            _db.SessionParticipants.Add(participant);
            joined = true;
        }
        else if (participant is not null && !isOwningTeacher)
        {
            participant.JoinedAt ??= now;
            participant.LeftAt = null;
            joined = true;
        }
        else if (participant is not null && !pastPlannedEnd &&
                 participant.JoinedAt is null && participant.LeftAt is null && participant.DeclinedAt is null)
        {
            // The owning teacher took part by code and their agent or extension
            // is joining (#410). Their dashboard's live page sends this same
            // call to subscribe, so for them it only ever completes a pending
            // join: it never brings back a participation they left or declined.
            // Taking part again goes through join-by-code, which clears those.
            participant.JoinedAt = now;
            joined = true;
        }

        await _db.SaveChangesAsync(ct);
        if (participant is not null)
            _activeParticipants.Update(participant);

        // The agent and the extension both join, so this join may be the
        // extension's alone. Start waiting for the agent's first heartbeat; the
        // monitor reports it missing if none arrives in time (#444). Already
        // tracked, as when the agent joined first, it stays as it is.
        if (joined)
            _heartbeats.ExpectAgent(session.Id, user.Id, now);

        if (isOwningTeacher)
        {
            // Only the owning teacher's connections subscribe to the session
            // group (#366): it carries their roster feed, with each student's
            // name, the URLs they ask to open and their tamper flags. A
            // student's join records them as joined without subscribing them;
            // what their agent and extension listen for goes to their user group.
            // Apart from completing a pending join (above), the teacher's join
            // changes nothing else, so calling it again, from the same
            // connection or a new one, only (re)subscribes it.
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(session.Id), ct);
        }

        if (joined)
        {
            // Tell the teacher's roster a participant just joined (#100). The
            // owning teacher's own subscribe doesn't emit; their join as a
            // participant does, since the roster lists them once they take part.
            await _broadcaster.ParticipantStateChangedAsync(
                new ParticipantStateChangedPayload(
                    session.Id, user.Id, user.DisplayName,
                    nameof(ParticipantLiveState.Joined), now),
                ct);
        }

        return new JoinSessionResult(session.Id, user.Id);
    }

    public async Task LeaveSession(Guid sessionId)
    {
        var ct = Context.ConnectionAborted;
        var user = await ResolveCurrentUserAsync(ct);

        var participant = await _db.SessionParticipants
            .FirstOrDefaultAsync(p => p.SessionId == sessionId && p.UserId == user.Id, ct);
        if (participant is not null)
        {
            var leftAt = _clock.GetUtcNow();
            participant.LeftAt = leftAt;
            await _db.SaveChangesAsync(ct);
            _activeParticipants.Update(participant);

            // The agent and the extension stop pinging once the student has
            // left, so drop their heartbeat tracking, as AgentKilled does: left
            // in place, their last pings would age into a HeartbeatLost and an
            // extension_silent flag for a student who left normally (#448).
            // After the cache update above, so a ping arriving from now on is
            // rejected rather than tracked again.
            _heartbeats.Clear(sessionId, user.Id);

            await _broadcaster.ParticipantStateChangedAsync(
                new ParticipantStateChangedPayload(
                    sessionId, user.Id, user.DisplayName,
                    nameof(ParticipantLiveState.Left), leftAt),
                ct);

            // The agent called this; the student's extension would otherwise
            // go on filtering and pinging a session they left (#462).
            await _broadcaster.ParticipationEndedAsync(sessionId, user.Id, ct);
        }

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(sessionId), ct);
    }

    public async Task DeclineSession(DeclineSessionRequest request)
    {
        var ct = Context.ConnectionAborted;
        var user = await ResolveCurrentUserAsync(ct);

        var session = await _db.Sessions
            .FirstOrDefaultAsync(s => s.Id == request.SessionId, ct);
        if (session is null || session.EndedAt is not null)
            throw new HubException("Session not found or already ended.");

        var now = _clock.GetUtcNow();

        var participant = await _db.SessionParticipants
            .FirstOrDefaultAsync(p => p.SessionId == session.Id && p.UserId == user.Id, ct);
        if (participant is null)
        {
            participant = new SessionParticipant
            {
                SessionId = session.Id,
                UserId = user.Id,
                DeclinedAt = now,
            };
            _db.SessionParticipants.Add(participant);
        }
        else
        {
            participant.DeclinedAt ??= now;
        }

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? "user_cancelled" : request.Reason!;
        var payloadJson = System.Text.Json.JsonSerializer.Serialize(new { reason });

        _db.Events.Add(new Event
        {
            SessionId = session.Id,
            UserId = user.Id,
            Kind = EventKind.JoinDeclined,
            PayloadJson = payloadJson,
            OccurredAt = now,
        });

        await _db.SaveChangesAsync(ct);
        // Declining takes the student out of the session even when their
        // extension already joined it: it joins on SessionStarted without
        // waiting for the student's answer in the agent, which set JoinedAt
        // (#462). So their pings are turned away from now on.
        _activeParticipants.Update(participant);

        // A student can decline a session their agent was already in: when a
        // restarted agent's silent rejoin fails, its catch-up offers that
        // session again (#356). An agent doesn't ping a session it declined, so
        // drop the tracking its earlier pings left, which would otherwise age
        // into a HeartbeatLost for a student the roster shows as Declined (#448).
        _heartbeats.Clear(session.Id, user.Id);

        await _broadcaster.ParticipantStateChangedAsync(
            new ParticipantStateChangedPayload(
                session.Id, user.Id, user.DisplayName,
                nameof(ParticipantLiveState.Declined), now),
            ct);

        // The extension may be enforcing the session already, as above (#462).
        await _broadcaster.ParticipationEndedAsync(session.Id, user.Id, ct);
    }

    public Task Heartbeat(Guid sessionId) => RecordHeartbeatAsync(sessionId, WitnessSource.Agent);

    /// <summary>
    /// The Edge extension's liveness ping (#149). A separate hub method rather
    /// than an extra parameter on <see cref="Heartbeat"/> so the already-deployed
    /// agent's single-argument <c>Heartbeat</c> invocation stays wire-compatible.
    /// Recorded under <see cref="WitnessSource.Extension"/> so it tracks
    /// independently of the agent: the extension going silent is the absence-net
    /// (<c>extension_silent</c>), not an agent HeartbeatLost.
    /// </summary>
    public Task ExtensionHeartbeat(Guid sessionId) => RecordHeartbeatAsync(sessionId, WitnessSource.Extension);

    private async Task RecordHeartbeatAsync(Guid sessionId, WitnessSource source)
    {
        var ct = Context.ConnectionAborted;
        var user = await ResolveCurrentUserAsync(ct);

        // The hub is the only liveness witness, so we don't want a stale or
        // finished session keeping participant slots warm. Confirm the
        // participant is actively joined before recording the ping — mirrors
        // ReportEvent's check. Answered from memory (#342): this runs on every
        // ping from every agent and extension.
        if (!await _activeParticipants.IsActiveAsync(_db.SessionParticipants.AsNoTracking(), sessionId, user.Id, ct))
            throw new HubException("Not an active participant of this session.");

        var now = _clock.GetUtcNow();
        _heartbeats.Record(sessionId, user.Id, now, source);

        // A pinging extension means its student's agent should be pinging too.
        // JoinSession already started waiting for it, but a backend restart
        // empties the tracker, and the extension keeps pinging without joining
        // again, so its pings start the wait anew (#444).
        if (source == WitnessSource.Extension)
            _heartbeats.ExpectAgent(sessionId, user.Id, now);
    }

    /// <summary>
    /// Whether the caller is still in the session: it hasn't ended and the
    /// caller is an active participant — the condition under which
    /// <see cref="Heartbeat"/> and <see cref="ReportEvent"/> accept the caller's
    /// calls for it. The agent and the extension ask after every reconnect
    /// (#354): <c>SessionEnded</c> reaches only the connections open when it is
    /// sent, so a session that ended while a client was offline — a network
    /// drop, a backend restart, a laptop asleep through the automatic end —
    /// would otherwise stay in force on that client. The same goes for a
    /// student who left, declined or quit the agent while their extension was
    /// offline: they are out of the session too, and miss the
    /// <c>ParticipationEnded</c> that says so (#462). Read from the database
    /// rather than <see cref="ActiveParticipantCache"/>: it runs once per
    /// reconnect, not per ping, and decides whether the student leaves focus
    /// mode. A session past its planned end (#447) counts as ended, so the
    /// sweep's interval leaves no gap after the backend restarts.
    /// </summary>
    public async Task<bool> IsInSession(Guid sessionId)
    {
        var ct = Context.ConnectionAborted;
        var user = await ResolveCurrentUserAsync(ct);
        var active = await _db.SessionParticipants.AsNoTracking()
            .Where(ActiveParticipantCache.IsActive(sessionId, user.Id))
            .Select(p => new { p.Session!.EndsAt })
            .FirstOrDefaultAsync(ct);
        if (active is null)
            return false;

        if (active.EndsAt is { } endsAt)
            _endSchedule.Schedule(sessionId, endsAt);
        return !Session.IsPastPlannedEnd(active.EndsAt, _clock.GetUtcNow());
    }

    /// <summary>
    /// The running session the caller is a participant of and hasn't declined
    /// or left, as the <see cref="SessionStartedPayload"/> its start sent, or
    /// null; the most recently started one if there are several. The start-side
    /// counterpart of <see cref="IsInSession"/>: the agent and the extension
    /// ask after every (re)connect and handle the answer as a
    /// <c>SessionStarted</c> they missed (#356). That broadcast goes to the
    /// user group, so it reaches only the connections in it when it is sent:
    /// not a client that was offline then, and not one that had only just
    /// connected, because a client's start completes on the handshake reply,
    /// which the server sends before <see cref="OnConnectedAsync"/> adds the
    /// connection to its user group. An invocation is dispatched only once
    /// OnConnectedAsync has finished, so a session saved before this query runs
    /// is in its answer, and any later one's broadcast finds the connection in
    /// its group. A session past its planned end (#447) is left out: it is
    /// over, so an agent or extension starting after the lesson stays out of it.
    /// </summary>
    public async Task<SessionStartedPayload?> GetStartedSession()
    {
        var ct = Context.ConnectionAborted;
        var user = await ResolveCurrentUserAsync(ct);

        var rows = await _db.SessionParticipants.AsNoTracking()
            .Where(p => p.UserId == user.Id &&
                        p.DeclinedAt == null &&
                        p.LeftAt == null &&
                        p.Session!.EndedAt == null)
            .Select(p => new { p.Session!.Id, p.Session.ClassId, p.Session.StartedAt, p.Session.EndsAt, p.Session.JoinCode })
            .ToListAsync(ct);
        var now = _clock.GetUtcNow();
        foreach (var row in rows)
        {
            if (row.EndsAt is { } endsAt)
                _endSchedule.Schedule(row.Id, endsAt);
        }
        // SQLite (dev + tests) can't ORDER BY a DateTimeOffset; sort in memory,
        // as /sessions/rejoinable does.
        var latest = rows
            .Where(s => !Session.IsPastPlannedEnd(s.EndsAt, now))
            .MaxBy(s => s.StartedAt);
        if (latest is null)
            return null;

        // The allowlist a join-by-code SessionStarted carries: the session's
        // bundles plus its whole-class grants (#101).
        var allowlist = await _allowlist.ExpandForSessionAsync(latest.Id, ct);
        return new SessionStartedPayload(
            latest.Id, latest.ClassId, latest.StartedAt, latest.JoinCode, allowlist.Apps, allowlist.Domains);
    }

    public async Task ReportEvent(ReportEventRequest request)
    {
        var ct = Context.ConnectionAborted;
        var user = await ResolveCurrentUserAsync(ct);

        if (!await _activeParticipants.IsActiveAsync(_db.SessionParticipants.AsNoTracking(), request.SessionId, user.Id, ct))
            throw new HubException("Not a participant of this session.");

        if (!Enum.TryParse<EventKind>(request.Kind, ignoreCase: true, out var kind))
            throw new HubException($"Unknown event kind '{request.Kind}'.");

        var payloadJson = string.IsNullOrWhiteSpace(request.PayloadJson) ? "{}" : request.PayloadJson!;
        var occurredAt = request.OccurredAt ?? _clock.GetUtcNow();

        var @event = new Event
        {
            SessionId = request.SessionId,
            UserId = user.Id,
            Kind = kind,
            PayloadJson = payloadJson,
            OccurredAt = occurredAt,
        };
        _db.Events.Add(@event);
        await _db.SaveChangesAsync(ct);

        // UnblockRequest is the one event kind that triggers a live teacher-
        // facing push (#73). The Event row above is the authoritative source —
        // the broadcast is best-effort and skipped if the payload doesn't
        // parse to a usable host; the dashboard's GET endpoint will still
        // surface it on a reload.
        if (kind == EventKind.UnblockRequest)
        {
            var parsed = TryParseUnblockRequestPayload(payloadJson);
            if (parsed is not null)
            {
                await _broadcaster.UnblockRequestedAsync(
                    new UnblockRequestedPayload(
                        request.SessionId,
                        user.Id,
                        user.DisplayName,
                        parsed.Host,
                        parsed.Url,
                        occurredAt),
                    ct);
            }
            else
            {
                _log.LogWarning(
                    "UnblockRequest from {UserId} on session {SessionId} had unparseable payload — broadcast skipped.",
                    user.Id, request.SessionId);
            }
        }
        // TamperDetected pushes a live flag to the teacher's roster (#105).
        // Unlike UnblockRequest we never skip the broadcast on a bad payload —
        // the tamper happened regardless of whether the sub-kind parsed, and
        // soft enforcement (§5.4) is about making it visible.
        else if (kind == EventKind.TamperDetected)
        {
            await _broadcaster.TamperDetectedAsync(
                new TamperDetectedPayload(
                    request.SessionId,
                    user.Id,
                    user.DisplayName,
                    ExtractTamperKind(payloadJson),
                    occurredAt),
                ct);
        }
        // AgentKilled (#110): the student deliberately quit the agent mid-session.
        // Mark the participant left so the roster resolves to Left immediately —
        // the live-state resolver keys off LeftAt, and the dashboard re-fetches
        // that snapshot on the broadcast below, so without it the Left transition
        // would flash and then revert to Joined. Drop heartbeat tracking too, so
        // the now-dead agent's last ping can't age into a spurious HeartbeatLost
        // after they've already left. The persisted Event row above is the
        // durable record; this is the live push.
        else if (kind == EventKind.AgentKilled)
        {
            var participant = await _db.SessionParticipants
                .FirstOrDefaultAsync(p => p.SessionId == request.SessionId && p.UserId == user.Id, ct);
            if (participant is not null)
            {
                if (participant.LeftAt is null)
                {
                    participant.LeftAt = occurredAt;
                    await _db.SaveChangesAsync(ct);
                }
                _activeParticipants.Update(participant);
            }

            _heartbeats.Clear(request.SessionId, user.Id);

            await _broadcaster.ParticipantStateChangedAsync(
                new ParticipantStateChangedPayload(
                    request.SessionId, user.Id, user.DisplayName,
                    nameof(ParticipantLiveState.Left), occurredAt),
                ct);

            // The extension outlives the agent, and the student is out of the
            // session now, as after a leave (#462).
            await _broadcaster.ParticipationEndedAsync(request.SessionId, user.Id, ct);
        }
    }

    private static UnblockRequestPayloadShape? TryParseUnblockRequestPayload(string payloadJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

            var url = root.TryGetProperty("url", out var u) && u.ValueKind == System.Text.Json.JsonValueKind.String
                ? u.GetString() ?? string.Empty
                : string.Empty;
            var host = root.TryGetProperty("host", out var h) && h.ValueKind == System.Text.Json.JsonValueKind.String
                ? h.GetString() ?? string.Empty
                : string.Empty;

            host = host.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(host)) return null;
            return new UnblockRequestPayloadShape(url, host);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private sealed record UnblockRequestPayloadShape(string Url, string Host);

    /// <summary>
    /// Pulls the tamper sub-kind out of a TamperDetected payload
    /// (<c>{"kind":"…"}</c>). Defaults to <c>"unknown"</c> rather than throwing —
    /// the event is still worth flagging even if the client sent a garbled
    /// payload (#105).
    /// </summary>
    private static string ExtractTamperKind(string payloadJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("kind", out var k) &&
                k.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var kind = k.GetString();
                if (!string.IsNullOrWhiteSpace(kind))
                    return kind.Trim();
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Fall through to the default — a malformed payload still flags.
        }

        return "unknown";
    }

    private async Task<User> ResolveCurrentUserAsync(CancellationToken ct)
    {
        if (Context.Items.TryGetValue(ResolvedUserContextItemKey, out var cached) && cached is User cachedUser)
            return cachedUser;

        var principal = Context.User
            ?? throw new HubException("No authenticated user on connection.");

        var entraOid = TryGetDevImpersonationOid() ?? GetTokenOid(principal)
            ?? throw new HubException("Token missing oid claim.");

        var user = await _users.FindByEntraOidAsync(entraOid, ct)
            ?? throw new HubException("User not provisioned. Sign in via /me first.");

        Context.Items[ResolvedUserContextItemKey] = user;
        return user;
    }

    private async Task<User?> TryResolveCurrentUserAsync(CancellationToken ct)
    {
        if (Context.Items.TryGetValue(ResolvedUserContextItemKey, out var cached) && cached is User cachedUser)
            return cachedUser;

        var principal = Context.User;
        if (principal is null)
            return null;

        var entraOid = TryGetDevImpersonationOid() ?? GetTokenOid(principal);
        if (entraOid is null)
            return null;

        var user = await _users.FindByEntraOidAsync(entraOid.Value, ct);
        if (user is not null)
            Context.Items[ResolvedUserContextItemKey] = user;
        return user;
    }

    private static Guid? GetTokenOid(System.Security.Claims.ClaimsPrincipal principal)
    {
        var oidValue = principal.FindFirst(EntraOidShortClaim)?.Value
                       ?? principal.FindFirst(EntraOidLongClaim)?.Value;
        return Guid.TryParse(oidValue, out var entraOid) ? entraOid : null;
    }

    private Guid? TryGetDevImpersonationOid()
    {
        if (!_env.IsDevelopment()) return null;

        var http = Context.GetHttpContext();
        if (http is null) return null;

        // Browser-hosted SignalR clients (the Edge extension) cannot attach
        // custom headers to the WebSocket upgrade, so the dev-impersonation
        // value can also arrive as a query-string parameter — matches the
        // fallback in DevImpersonationAuthHandler.
        var raw = http.Request.Headers[DevImpersonateOidHeader].ToString();
        if (string.IsNullOrWhiteSpace(raw))
            raw = http.Request.Query[Auth.DevImpersonationAuthHandler.QueryParamName].ToString();

        return Guid.TryParse(raw, out var oid) ? oid : null;
    }
}
