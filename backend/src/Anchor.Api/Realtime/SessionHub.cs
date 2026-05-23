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

    private const string EntraOidShortClaim = "oid";
    private const string EntraOidLongClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    private readonly AnchorDbContext _db;
    private readonly IUserStore _users;
    private readonly TimeProvider _clock;

    public SessionHub(AnchorDbContext db, IUserStore users, TimeProvider clock)
    {
        _db = db;
        _users = users;
        _clock = clock;
    }

    public static string GroupName(Guid sessionId) => $"session:{sessionId:D}";

    public static string UserGroupName(Guid userId) => $"user:{userId:D}";

    public override async Task OnConnectedAsync()
    {
        var ct = Context.ConnectionAborted;
        var user = await TryResolveCurrentUserAsync(ct);
        if (user is not null)
            await Groups.AddToGroupAsync(Context.ConnectionId, UserGroupName(user.Id), ct);
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

        var @event = new Event
        {
            SessionId = request.SessionId,
            UserId = user.Id,
            Kind = kind,
            PayloadJson = string.IsNullOrWhiteSpace(request.PayloadJson) ? "{}" : request.PayloadJson!,
            OccurredAt = request.OccurredAt ?? _clock.GetUtcNow(),
        };
        _db.Events.Add(@event);
        await _db.SaveChangesAsync(ct);
    }

    private async Task<User> ResolveCurrentUserAsync(CancellationToken ct)
    {
        var principal = Context.User
            ?? throw new HubException("No authenticated user on connection.");

        var oidValue = principal.FindFirst(EntraOidShortClaim)?.Value
                       ?? principal.FindFirst(EntraOidLongClaim)?.Value;
        if (oidValue is null || !Guid.TryParse(oidValue, out var entraOid))
            throw new HubException("Token missing oid claim.");

        return await _users.FindByEntraOidAsync(entraOid, ct)
            ?? throw new HubException("User not provisioned. Sign in via /me first.");
    }

    private async Task<User?> TryResolveCurrentUserAsync(CancellationToken ct)
    {
        var principal = Context.User;
        if (principal is null)
            return null;

        var oidValue = principal.FindFirst(EntraOidShortClaim)?.Value
                       ?? principal.FindFirst(EntraOidLongClaim)?.Value;
        if (oidValue is null || !Guid.TryParse(oidValue, out var entraOid))
            return null;

        return await _users.FindByEntraOidAsync(entraOid, ct);
    }
}
