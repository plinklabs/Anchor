using Anchor.Api.Realtime;
using Anchor.Domain.Classes;
using Anchor.Domain.Events;
using Anchor.Domain.Sessions;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Anchor.Api.Controllers;

[ApiController]
[Authorize]
[Route("sessions")]
public sealed class SessionsController : ControllerBase
{
    private const int JoinCodeGenerationAttempts = 10;
    private const int RecentEventsLimit = 50;

    private readonly AnchorDbContext _db;
    private readonly IUserStore _users;
    private readonly ISessionBroadcaster _broadcaster;
    private readonly TimeProvider _clock;

    public SessionsController(
        AnchorDbContext db,
        IUserStore users,
        ISessionBroadcaster broadcaster,
        TimeProvider clock)
    {
        _db = db;
        _users = users;
        _broadcaster = broadcaster;
        _clock = clock;
    }

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.Teacher)]
    [ProducesResponseType(typeof(StartSessionResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StartSessionResponse>> Start(
        [FromBody] StartSessionRequest request,
        CancellationToken cancellationToken)
    {
        if (!User.TryGetEntraOid(out var entraOid))
            return Unauthorized();

        var caller = await _users.FindByEntraOidAsync(entraOid, cancellationToken);
        if (caller is null)
            return Unauthorized();

        if (!Enum.TryParse<SessionMode>(request.Mode, ignoreCase: true, out var mode))
            return ValidationProblem($"Unknown session mode '{request.Mode}'.");

        var @class = await _db.Classes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.ClassId, cancellationToken);
        if (@class is null)
            return NotFound();

        var callerTeaches = await _db.ClassMemberships.AsNoTracking().AnyAsync(
            m => m.ClassId == request.ClassId && m.UserId == caller.Id && m.Role == ClassMembershipRole.Teacher,
            cancellationToken);
        if (!callerTeaches)
            return Forbid();

        var bundleIds = (request.BundleIds ?? Array.Empty<Guid>()).Distinct().ToArray();
        if (bundleIds.Length > 0)
        {
            var existingCount = await _db.Bundles.AsNoTracking().CountAsync(b => bundleIds.Contains(b.Id), cancellationToken);
            if (existingCount != bundleIds.Length)
                return ValidationProblem("One or more bundle ids do not exist.");
        }

        var rosterUserIds = await _db.ClassMemberships
            .AsNoTracking()
            .Where(m => m.ClassId == request.ClassId)
            .Select(m => new { m.UserId, m.Role })
            .ToListAsync(cancellationToken);
        var memberIds = rosterUserIds
            .Where(m => m.Role == ClassMembershipRole.Member)
            .Select(m => m.UserId)
            .ToList();
        var broadcastRecipientIds = rosterUserIds.Select(m => m.UserId).ToList();

        var joinCode = await GenerateUniqueJoinCodeAsync(cancellationToken);
        var now = _clock.GetUtcNow();

        var session = new Session
        {
            TeacherId = caller.Id,
            ClassId = request.ClassId,
            Mode = mode,
            StartedAt = now,
            JoinCode = joinCode,
        };

        _db.Sessions.Add(session);

        foreach (var bundleId in bundleIds)
        {
            _db.SessionBundles.Add(new SessionBundle { SessionId = session.Id, BundleId = bundleId });
        }

        foreach (var memberId in memberIds)
        {
            _db.SessionParticipants.Add(new SessionParticipant
            {
                SessionId = session.Id,
                UserId = memberId,
            });
        }

        await _db.SaveChangesAsync(cancellationToken);

        await _broadcaster.SessionStartedAsync(
            new SessionStartedPayload(session.Id, session.ClassId, session.Mode.ToString(), session.StartedAt, session.JoinCode),
            broadcastRecipientIds,
            cancellationToken);

        var response = new StartSessionResponse(session.Id, session.ClassId, session.Mode, session.StartedAt, session.JoinCode);
        return CreatedAtAction(nameof(Get), new { id = session.Id }, response);
    }

    [HttpPost("{id:guid}/end")]
    [Authorize(Policy = AuthorizationPolicies.Teacher)]
    [ProducesResponseType(typeof(EndSessionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EndSessionResponse>> End(Guid id, CancellationToken cancellationToken)
    {
        if (!User.TryGetEntraOid(out var entraOid))
            return Unauthorized();

        var caller = await _users.FindByEntraOidAsync(entraOid, cancellationToken);
        if (caller is null)
            return Unauthorized();

        var session = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (session is null)
            return NotFound();

        if (session.TeacherId != caller.Id)
            return Forbid();

        if (session.EndedAt is null)
        {
            session.EndedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(cancellationToken);
            await _broadcaster.SessionEndedAsync(session.Id, cancellationToken);
        }

        return Ok(new EndSessionResponse(session.Id, session.EndedAt!.Value));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(SessionDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SessionDetailResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        if (!User.TryGetEntraOid(out var entraOid))
            return Unauthorized();

        var caller = await _users.FindByEntraOidAsync(entraOid, cancellationToken);
        if (caller is null)
            return Unauthorized();

        var session = await _db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (session is null)
            return NotFound();

        var isParticipant = await _db.SessionParticipants.AsNoTracking()
            .AnyAsync(p => p.SessionId == id && p.UserId == caller.Id, cancellationToken);
        if (session.TeacherId != caller.Id && !isParticipant)
            return Forbid();

        var participants = await _db.SessionParticipants.AsNoTracking()
            .Where(p => p.SessionId == id)
            .OrderBy(p => p.User!.DisplayName)
            .Select(p => new SessionParticipantSummary(
                p.UserId,
                p.User!.DisplayName,
                p.JoinedAt,
                p.DeclinedAt,
                p.LeftAt))
            .ToListAsync(cancellationToken);

        var recentEventRows = await _db.Events.AsNoTracking()
            .Where(e => e.SessionId == id)
            .Select(e => new { e.Id, e.UserId, e.Kind, e.PayloadJson, e.OccurredAt })
            .ToListAsync(cancellationToken);
        var recentEvents = recentEventRows
            .OrderByDescending(e => e.OccurredAt)
            .Take(RecentEventsLimit)
            .Select(e => new SessionEventSummary(e.Id, e.UserId, e.Kind, e.PayloadJson, e.OccurredAt))
            .ToList();

        return Ok(new SessionDetailResponse(
            session.Id,
            session.ClassId,
            session.TeacherId,
            session.Mode,
            session.StartedAt,
            session.EndedAt,
            session.JoinCode,
            participants,
            recentEvents));
    }

    [HttpGet("active")]
    [ProducesResponseType(typeof(IReadOnlyList<SessionSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<SessionSummary>>> Active(CancellationToken cancellationToken)
    {
        if (!User.TryGetEntraOid(out var entraOid))
            return Unauthorized();

        var caller = await _users.FindByEntraOidAsync(entraOid, cancellationToken);
        if (caller is null)
            return Unauthorized();

        var query = _db.Sessions.AsNoTracking().Where(s => s.EndedAt == null);

        query = caller.Role == UserRole.Teacher
            ? query.Where(s => s.TeacherId == caller.Id)
            : query.Where(s => _db.SessionParticipants.Any(p => p.SessionId == s.Id && p.UserId == caller.Id));

        var rows = await query
            .Select(s => new SessionSummary(s.Id, s.ClassId, s.TeacherId, s.Mode, s.StartedAt, s.EndedAt, s.JoinCode))
            .ToListAsync(cancellationToken);
        var sessions = rows.OrderByDescending(s => s.StartedAt).ToList();

        return Ok(sessions);
    }

    private async Task<string> GenerateUniqueJoinCodeAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < JoinCodeGenerationAttempts; attempt++)
        {
            var code = Random.Shared.Next(0, 1_000_000).ToString("D6");
            var exists = await _db.Sessions.AsNoTracking().AnyAsync(s => s.JoinCode == code, cancellationToken);
            if (!exists)
                return code;
        }

        throw new InvalidOperationException("Failed to allocate a unique 6-digit join code after multiple attempts.");
    }
}

public sealed record StartSessionRequest(Guid ClassId, string Mode, IReadOnlyList<Guid>? BundleIds);

public sealed record StartSessionResponse(
    Guid Id,
    Guid ClassId,
    SessionMode Mode,
    DateTimeOffset StartedAt,
    string JoinCode);

public sealed record EndSessionResponse(Guid Id, DateTimeOffset EndedAt);

public sealed record SessionSummary(
    Guid Id,
    Guid ClassId,
    Guid TeacherId,
    SessionMode Mode,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string JoinCode);

public sealed record SessionDetailResponse(
    Guid Id,
    Guid ClassId,
    Guid TeacherId,
    SessionMode Mode,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string JoinCode,
    IReadOnlyList<SessionParticipantSummary> Participants,
    IReadOnlyList<SessionEventSummary> RecentEvents);

public sealed record SessionParticipantSummary(
    Guid UserId,
    string DisplayName,
    DateTimeOffset? JoinedAt,
    DateTimeOffset? DeclinedAt,
    DateTimeOffset? LeftAt);

public sealed record SessionEventSummary(
    Guid Id,
    Guid UserId,
    EventKind Kind,
    string PayloadJson,
    DateTimeOffset OccurredAt);
