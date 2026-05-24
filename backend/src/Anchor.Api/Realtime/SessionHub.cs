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
    private readonly ISessionBroadcaster _broadcaster;
    private readonly ILogger<SessionHub> _log;

    public SessionHub(
        AnchorDbContext db,
        IUserStore users,
        TimeProvider clock,
        IHostEnvironment env,
        HeartbeatTracker heartbeats,
        ISessionBroadcaster broadcaster,
        ILogger<SessionHub> log)
    {
        _db = db;
        _users = users;
        _clock = clock;
        _env = env;
        _heartbeats = heartbeats;
        _broadcaster = broadcaster;
        _log = log;
    }

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

        // The session-owning teacher is not stored as a SessionParticipant
        // (they're the TeacherId). Let them subscribe to their own session
        // without a join code or a participant row.
        var isOwningTeacher = session.TeacherId == user.Id;

        var participant = await _db.SessionParticipants
            .FirstOrDefaultAsync(p => p.SessionId == session.Id && p.UserId == user.Id, ct);

        var now = _clock.GetUtcNow();
        if (participant is null && !isOwningTeacher)
        {
            // Loose mode: any signed-in user with the correct join code may join.
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
        }
        else if (participant is not null)
        {
            participant.JoinedAt ??= now;
            participant.LeftAt = null;
        }

        await _db.SaveChangesAsync(ct);
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(session.Id), ct);
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
            participant.LeftAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
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
    }

    public async Task Heartbeat(Guid sessionId)
    {
        var ct = Context.ConnectionAborted;
        var user = await ResolveCurrentUserAsync(ct);

        // The hub is the agent's only liveness witness, so we don't want a
        // stale or finished session keeping participant slots warm. Confirm
        // the participant is actively joined before recording the ping —
        // mirrors ReportEvent's check.
        var isActiveParticipant = await _db.SessionParticipants.AsNoTracking().AnyAsync(
            p => p.SessionId == sessionId &&
                 p.UserId == user.Id &&
                 p.JoinedAt != null &&
                 p.LeftAt == null,
            ct);
        if (!isActiveParticipant)
            throw new HubException("Not an active participant of this session.");

        _heartbeats.Record(sessionId, user.Id, _clock.GetUtcNow());
    }

    public async Task ReportEvent(ReportEventRequest request)
    {
        var ct = Context.ConnectionAborted;
        var user = await ResolveCurrentUserAsync(ct);

        var isActiveParticipant = await _db.SessionParticipants.AnyAsync(
            p => p.SessionId == request.SessionId &&
                 p.UserId == user.Id &&
                 p.JoinedAt != null &&
                 p.LeftAt == null,
            ct);
        if (!isActiveParticipant)
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
