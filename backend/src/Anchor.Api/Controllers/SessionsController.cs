using Anchor.Api.Realtime;
using Anchor.Api.Sessions;
using Anchor.Domain.Classes;
using Anchor.Domain.Events;
using Anchor.Domain.Sessions;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Anchor.Api.Controllers;

[ApiController]
[Authorize]
[Route("sessions")]
public sealed class SessionsController : ControllerBase
{
    private const int JoinCodeGenerationAttempts = 10;
    private const int RecentEventsLimit = 50;

    /// <summary>
    /// A typed join code stops being honoured once the session is this old,
    /// even if it has not been ended. Prevents a leaked code from a stale
    /// (probably-forgotten) session granting access hours later.
    /// </summary>
    public static readonly TimeSpan JoinCodeFreshnessWindow = TimeSpan.FromHours(4);

    private readonly AnchorDbContext _db;
    private readonly IUserStore _users;
    private readonly ISessionBroadcaster _broadcaster;
    private readonly TimeProvider _clock;
    private readonly JoinByCodeRateLimiter _joinByCodeLimiter;
    private readonly ISessionAllowlistExpander _allowlist;
    private readonly ParticipantLiveStateResolver _liveState;

    public SessionsController(
        AnchorDbContext db,
        IUserStore users,
        ISessionBroadcaster broadcaster,
        TimeProvider clock,
        JoinByCodeRateLimiter joinByCodeLimiter,
        ISessionAllowlistExpander allowlist,
        ParticipantLiveStateResolver liveState)
    {
        _db = db;
        _users = users;
        _broadcaster = broadcaster;
        _clock = clock;
        _joinByCodeLimiter = joinByCodeLimiter;
        _allowlist = allowlist;
        _liveState = liveState;
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

        var expanded = await _allowlist.ExpandAsync(bundleIds, cancellationToken);
        await _broadcaster.SessionStartedAsync(
            BuildStartedPayload(session, expanded),
            broadcastRecipientIds,
            cancellationToken);

        var response = new StartSessionResponse(session.Id, session.ClassId, session.StartedAt, session.JoinCode);
        return CreatedAtAction(nameof(Get), new { id = session.Id }, response);
    }

    private static SessionStartedPayload BuildStartedPayload(Session session, ExpandedAllowlist allowlist) =>
        new(
            session.Id,
            session.ClassId,
            session.StartedAt,
            session.JoinCode,
            allowlist.Apps,
            allowlist.Domains);

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
            await AggregateEventSummariesAsync(session.Id, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await _broadcaster.SessionEndedAsync(session.Id, cancellationToken);
        }

        return Ok(new EndSessionResponse(session.Id, session.EndedAt!.Value));
    }

    /// <summary>
    /// Aggregate this session's raw events into the
    /// <see cref="SessionEventSummary"/> table so the per-(student, kind)
    /// counts survive the 30-day raw-event prune (#77). Caller is responsible
    /// for the surrounding SaveChanges so the EndedAt update and the summary
    /// rows commit in a single transaction.
    /// </summary>
    private async Task AggregateEventSummariesAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        // SQLite (dev + tests) doesn't translate GROUP BY over DateTimeOffset
        // server-side, so materialise the rows first. Volume per session is
        // bounded (a class period generates hundreds, not millions of events).
        var rows = await _db.Events.AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .Select(e => new { e.UserId, e.Kind, e.OccurredAt })
            .ToListAsync(cancellationToken);

        var groups = rows
            .GroupBy(r => new { r.UserId, r.Kind })
            .Select(g => new SessionEventSummary
            {
                SessionId = sessionId,
                UserId = g.Key.UserId,
                Kind = g.Key.Kind,
                Count = g.Count(),
                FirstAt = g.Min(r => r.OccurredAt),
                LastAt = g.Max(r => r.OccurredAt),
            });

        foreach (var summary in groups)
        {
            _db.SessionEventSummaries.Add(summary);
        }
    }

    [HttpPut("{id:guid}/bundles")]
    [Authorize(Policy = AuthorizationPolicies.Teacher)]
    [ProducesResponseType(typeof(UpdateSessionBundlesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UpdateSessionBundlesResponse>> UpdateBundles(
        Guid id,
        [FromBody] UpdateSessionBundlesRequest request,
        CancellationToken cancellationToken)
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
        if (session.EndedAt is not null)
            return ValidationProblem("Session has ended.");

        var bundleIds = (request.BundleIds ?? Array.Empty<Guid>()).Distinct().ToArray();
        if (bundleIds.Length > 0)
        {
            var existingCount = await _db.Bundles.AsNoTracking().CountAsync(b => bundleIds.Contains(b.Id), cancellationToken);
            if (existingCount != bundleIds.Length)
                return ValidationProblem("One or more bundle ids do not exist.");
        }

        // Replace the session's bundle set wholesale — the picker sends the full
        // desired selection each time, not a delta.
        var existing = await _db.SessionBundles.Where(sb => sb.SessionId == id).ToListAsync(cancellationToken);
        _db.SessionBundles.RemoveRange(existing);
        foreach (var bundleId in bundleIds)
        {
            _db.SessionBundles.Add(new SessionBundle { SessionId = id, BundleId = bundleId });
        }
        await _db.SaveChangesAsync(cancellationToken);

        // Push the recomputed allowlist to every active participant. Each
        // student's domains differ by their own unblock grants (#73), so fold
        // those back in per-user and target the user group — a session-wide
        // broadcast would wipe grants the teacher already approved.
        var expanded = await _allowlist.ExpandAsync(bundleIds, cancellationToken);

        var activeParticipantIds = await _db.SessionParticipants.AsNoTracking()
            .Where(p => p.SessionId == id && p.JoinedAt != null && p.LeftAt == null && p.DeclinedAt == null)
            .Select(p => p.UserId)
            .ToListAsync(cancellationToken);

        var grantsByUser = (await _db.SessionUnblockGrants.AsNoTracking()
                .Where(g => g.SessionId == id)
                .Select(g => new { g.UserId, g.Host })
                .ToListAsync(cancellationToken))
            .GroupBy(g => g.UserId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Host).ToList());

        foreach (var participantId in activeParticipantIds)
        {
            var domains = expanded.Domains;
            if (grantsByUser.TryGetValue(participantId, out var hosts) && hosts.Count > 0)
            {
                // Suffix match mirrors the shape Unblock emits when granting a host.
                var withGrants = new List<AllowedDomainDto>(expanded.Domains);
                foreach (var host in hosts)
                    withGrants.Add(new AllowedDomainDto(AllowedDomainMatchTypes.Suffix, host));
                domains = DedupeDomains(withGrants);
            }

            await _broadcaster.SessionBundlesUpdatedAsync(
                participantId,
                new SessionBundlesUpdatedPayload(id, expanded.Apps, domains),
                cancellationToken);
        }

        var summaries = await _db.SessionBundles.AsNoTracking()
            .Where(sb => sb.SessionId == id)
            .Select(sb => new SessionBundleSummary(sb.BundleId, sb.Bundle!.Name))
            .ToListAsync(cancellationToken);

        return Ok(new UpdateSessionBundlesResponse(id, summaries));
    }

    /// <summary>
    /// Dedupes a domain list on (match-type, lowercase value). Mirrors
    /// <see cref="SessionAllowlistExpander"/>'s private dedup so grant-folded
    /// lists don't double-list a host already covered by a bundle.
    /// </summary>
    private static IReadOnlyList<AllowedDomainDto> DedupeDomains(IEnumerable<AllowedDomainDto> source)
    {
        var seen = new HashSet<(string, string)>();
        var result = new List<AllowedDomainDto>();
        foreach (var dto in source)
        {
            if (string.IsNullOrWhiteSpace(dto.Value)) continue;
            var key = (dto.MatchType, dto.Value.Trim().ToLowerInvariant());
            if (seen.Add(key))
                result.Add(dto);
        }
        return result;
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

        var className = await _db.Classes.AsNoTracking()
            .Where(c => c.Id == session.ClassId)
            .Select(c => c.Name)
            .FirstAsync(cancellationToken);

        // Live per-student state (#100) layers heartbeat liveness (in-memory,
        // not in the DB) on top of the lifecycle timestamps, so resolve it after
        // materialising. Name order is a stable secondary sort; the dashboard
        // re-groups by state.
        var participantRows = await _db.SessionParticipants.AsNoTracking()
            .Where(p => p.SessionId == id)
            .OrderBy(p => p.User!.DisplayName)
            .Select(p => new
            {
                p.UserId,
                DisplayName = p.User!.DisplayName,
                p.JoinedAt,
                p.DeclinedAt,
                p.LeftAt,
            })
            .ToListAsync(cancellationToken);
        var participants = participantRows
            .Select(p => new SessionParticipantSummary(
                p.UserId,
                p.DisplayName,
                p.JoinedAt,
                p.DeclinedAt,
                p.LeftAt,
                _liveState.Resolve(id, p.UserId, p.JoinedAt, p.DeclinedAt, p.LeftAt).ToString()))
            .ToList();

        var recentEventRows = await _db.Events.AsNoTracking()
            .Where(e => e.SessionId == id)
            .Select(e => new { e.Id, e.UserId, e.Kind, e.PayloadJson, e.OccurredAt })
            .ToListAsync(cancellationToken);
        var recentEvents = recentEventRows
            .OrderByDescending(e => e.OccurredAt)
            .Take(RecentEventsLimit)
            .Select(e => new SessionRecentEvent(e.Id, e.UserId, e.Kind, e.PayloadJson, e.OccurredAt))
            .ToList();

        // Per-(user, kind) aggregate written when the session ends. Once raw
        // events are pruned (#77) this is the only remaining record of what
        // happened — included for both live and ended sessions so the
        // dashboard never has to branch on retention state.
        var summaries = await _db.SessionEventSummaries.AsNoTracking()
            .Where(s => s.SessionId == id)
            .Select(s => new SessionEventSummaryDto(s.UserId, s.Kind, s.Count, s.FirstAt, s.LastAt))
            .ToListAsync(cancellationToken);

        // Bundles + grants survive the raw-event prune, so they remain usable
        // material on the past-sessions detail view even months later (#87
        // follow-up). Resolving the bundle/user names server-side spares the
        // dashboard a round-trip per row.
        var bundles = await _db.SessionBundles.AsNoTracking()
            .Where(sb => sb.SessionId == id)
            .Select(sb => new SessionBundleSummary(sb.BundleId, sb.Bundle!.Name))
            .ToListAsync(cancellationToken);

        // SQLite (dev + tests) can't ORDER BY a DateTimeOffset — materialise then
        // sort in memory, same pattern as /sessions/rejoinable.
        var grantRows = await _db.SessionUnblockGrants.AsNoTracking()
            .Where(g => g.SessionId == id)
            .Select(g => new SessionUnblockGrantDto(g.UserId, g.User!.DisplayName, g.Host, g.GrantedAt))
            .ToListAsync(cancellationToken);
        var grants = grantRows.OrderBy(g => g.GrantedAt).ToList();

        return Ok(new SessionDetailResponse(
            session.Id,
            session.ClassId,
            className,
            session.TeacherId,
            session.StartedAt,
            session.EndedAt,
            session.JoinCode,
            participants,
            recentEvents,
            summaries,
            bundles,
            grants));
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
            .Select(s => new SessionSummary(s.Id, s.ClassId, s.TeacherId, s.StartedAt, s.EndedAt, s.JoinCode))
            .ToListAsync(cancellationToken);
        var sessions = rows.OrderByDescending(s => s.StartedAt).ToList();

        return Ok(sessions);
    }

    [HttpGet("history")]
    [Authorize(Policy = AuthorizationPolicies.Teacher)]
    [ProducesResponseType(typeof(IReadOnlyList<SessionHistoryEntry>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<SessionHistoryEntry>>> History(
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        CancellationToken cancellationToken)
    {
        if (!User.TryGetEntraOid(out var entraOid))
            return Unauthorized();

        var caller = await _users.FindByEntraOidAsync(entraOid, cancellationToken);
        if (caller is null)
            return Unauthorized();

        var take = limit ?? 50;
        var skip = offset ?? 0;
        if (take < 1 || take > 200)
            return ValidationProblem("limit must be between 1 and 200.");
        if (skip < 0)
            return ValidationProblem("offset must be non-negative.");

        // Owner-scoped — issue #87 only surfaces sessions the calling teacher
        // started. We don't expose other teachers' history to keep the per-row
        // authorisation model the same as the live session-detail page.
        var rows = await _db.Sessions.AsNoTracking()
            .Where(s => s.EndedAt != null && s.TeacherId == caller.Id)
            .Join(_db.Classes.AsNoTracking(),
                s => s.ClassId,
                c => c.Id,
                (s, c) => new
                {
                    s.Id,
                    s.ClassId,
                    ClassName = c.Name,
                    s.TeacherId,
                    s.StartedAt,
                    s.EndedAt,
                })
            .ToListAsync(cancellationToken);

        // SQLite (dev + tests) can't ORDER BY a DateTimeOffset server side, so
        // sort + page in memory. Volume is bounded per teacher (one row per
        // ended session) so this stays cheap in practice — matches /sessions/rejoinable.
        var entries = rows
            .OrderByDescending(s => s.EndedAt!.Value)
            .Skip(skip)
            .Take(take)
            .Select(s => new SessionHistoryEntry(
                s.Id,
                s.ClassId,
                s.ClassName,
                s.TeacherId,
                s.StartedAt,
                s.EndedAt!.Value))
            .ToList();

        return Ok(entries);
    }

    [HttpGet("rejoinable")]
    [ProducesResponseType(typeof(IReadOnlyList<SessionStartedPayload>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<SessionStartedPayload>>> Rejoinable(CancellationToken cancellationToken)
    {
        // Drives the agent's post-restart rehydration (#54). Returns only the
        // sessions the caller is actively a member of right now -- non-ended,
        // JoinedAt set, LeftAt null, DeclinedAt null. The agent calls JoinSession
        // for every entry on the first hub Connected after startup. Server-side
        // filter so the agent never has to interpret participation state.
        //
        // The response shape matches the SessionStarted broadcast payload
        // (incl. the expanded allowlist, #70) so the agent's existing
        // SessionCoordinator can rejoin with the same rules a fresh
        // session-start would carry — without persisting the allowlist
        // to disk.
        if (!User.TryGetEntraOid(out var entraOid))
            return Unauthorized();

        var caller = await _users.FindByEntraOidAsync(entraOid, cancellationToken);
        if (caller is null)
            return Unauthorized();

        var rows = await _db.Sessions.AsNoTracking()
            .Where(s => s.EndedAt == null)
            .Where(s => _db.SessionParticipants.Any(p =>
                p.SessionId == s.Id &&
                p.UserId == caller.Id &&
                p.JoinedAt != null &&
                p.LeftAt == null &&
                p.DeclinedAt == null))
            .Select(s => new { s.Id, s.ClassId, s.StartedAt, s.JoinCode })
            .ToListAsync(cancellationToken);

        // SQLite (used by dev + tests) can't ORDER BY a DateTimeOffset server
        // side, so sort after materialisation. Production SqlServer would
        // happily do this in the query, but keeping a single path keeps the
        // test suite honest.
        var ordered = rows.OrderByDescending(s => s.StartedAt).ToList();

        var sessions = new List<SessionStartedPayload>(ordered.Count);
        foreach (var row in ordered)
        {
            var expanded = await _allowlist.ExpandForSessionAsync(row.Id, cancellationToken);
            sessions.Add(new SessionStartedPayload(
                row.Id,
                row.ClassId,
                row.StartedAt,
                row.JoinCode,
                expanded.Apps,
                expanded.Domains));
        }

        return Ok(sessions);
    }

    [HttpPost("join-by-code")]
    [ProducesResponseType(typeof(JoinByCodeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(JoinByCodeErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(JoinByCodeErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(JoinByCodeErrorResponse), StatusCodes.Status410Gone)]
    [ProducesResponseType(typeof(JoinByCodeErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<JoinByCodeResponse>> JoinByCode(
        [FromBody] JoinByCodeRequest request,
        CancellationToken cancellationToken)
    {
        if (!User.TryGetEntraOid(out var entraOid))
            return Unauthorized();

        var caller = await _users.FindByEntraOidAsync(entraOid, cancellationToken);
        if (caller is null)
            return Unauthorized();

        var code = (request.Code ?? string.Empty).Trim();
        if (code.Length != 6 || !code.All(char.IsDigit))
            return ValidationProblem("Join code must be 6 digits.");

        // Rate-limit checked up-front so a flood of attempts can't itself
        // become the DoS by saturating the DB.
        if (_joinByCodeLimiter.IsBlocked(caller.Id))
            return JoinByCodeError(StatusCodes.Status429TooManyRequests, "Too many attempts, try again shortly.");

        var session = await _db.Sessions.FirstOrDefaultAsync(s => s.JoinCode == code, cancellationToken);
        if (session is null)
        {
            _joinByCodeLimiter.RecordFailure(caller.Id);
            return JoinByCodeError(StatusCodes.Status404NotFound, "Code not found.");
        }

        var now = _clock.GetUtcNow();
        var expired = session.EndedAt is not null || (now - session.StartedAt) > JoinCodeFreshnessWindow;
        if (expired)
        {
            _joinByCodeLimiter.RecordFailure(caller.Id);
            return JoinByCodeError(StatusCodes.Status410Gone, "Session has ended.");
        }

        // "Already in a different session" = participant of some OTHER non-ended
        // session with JoinedAt set, LeftAt null, DeclinedAt null. Mirrors the
        // active-membership shape used by /sessions/rejoinable.
        var inOtherActive = await _db.SessionParticipants.AsNoTracking().AnyAsync(
            p => p.UserId == caller.Id &&
                 p.SessionId != session.Id &&
                 p.JoinedAt != null &&
                 p.LeftAt == null &&
                 p.DeclinedAt == null &&
                 p.Session!.EndedAt == null,
            cancellationToken);
        if (inOtherActive)
        {
            _joinByCodeLimiter.RecordFailure(caller.Id);
            return JoinByCodeError(StatusCodes.Status409Conflict, "You're already in a focus session.");
        }

        var participant = await _db.SessionParticipants.FirstOrDefaultAsync(
            p => p.SessionId == session.Id && p.UserId == caller.Id, cancellationToken);
        if (participant is null)
        {
            participant = new SessionParticipant
            {
                SessionId = session.Id,
                UserId = caller.Id,
            };
            _db.SessionParticipants.Add(participant);
        }
        else
        {
            // Idempotent rejoin: re-running the manual flow against the same
            // session (e.g. dialog retry) should not turn into an error.
            participant.LeftAt = null;
            participant.DeclinedAt = null;
        }

        await _db.SaveChangesAsync(cancellationToken);
        _joinByCodeLimiter.Reset(caller.Id);

        // Single-target SessionStarted: the agent's existing handler picks
        // this up and the #31 join-confirmation flow takes over. Keeping the
        // payload identical to the roster-based push means the agent needs
        // no new client-side branch.
        var expanded = await _allowlist.ExpandForSessionAsync(session.Id, cancellationToken);
        await _broadcaster.SessionStartedAsync(
            BuildStartedPayload(session, expanded),
            new[] { caller.Id },
            cancellationToken);

        return Ok(new JoinByCodeResponse(session.Id));
    }

    // ------- Unblock requests + grants (#73) -------

    [HttpGet("{id:guid}/unblock-requests")]
    [Authorize(Policy = AuthorizationPolicies.Teacher)]
    [ProducesResponseType(typeof(IReadOnlyList<UnblockRequestSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<UnblockRequestSummary>>> UnblockRequests(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!User.TryGetEntraOid(out var entraOid))
            return Unauthorized();

        var caller = await _users.FindByEntraOidAsync(entraOid, cancellationToken);
        if (caller is null)
            return Unauthorized();

        var session = await _db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (session is null)
            return NotFound();
        if (session.TeacherId != caller.Id)
            return Forbid();

        // Pull raw UnblockRequest events; group + filter happens in-memory
        // (SQLite + EF Core JSON access is dialect-fragile, and the volume per
        // session is small — tens of rows is the realistic upper bound).
        var eventRows = await _db.Events.AsNoTracking()
            .Where(e => e.SessionId == id && e.Kind == EventKind.UnblockRequest)
            .Select(e => new { e.UserId, e.PayloadJson, e.OccurredAt, e.User!.DisplayName })
            .ToListAsync(cancellationToken);

        var grants = await _db.SessionUnblockGrants.AsNoTracking()
            .Where(g => g.SessionId == id)
            .Select(g => new { g.UserId, g.Host })
            .ToListAsync(cancellationToken);
        var granted = grants
            .Select(g => (g.UserId, Host: g.Host.ToLowerInvariant()))
            .ToHashSet();

        var byHost = new Dictionary<string, List<UnblockRequestRequester>>(StringComparer.Ordinal);
        var hostFirstSeen = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var hostLatest = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var seenUserHost = new HashSet<(Guid, string)>();

        foreach (var row in eventRows.OrderBy(r => r.OccurredAt))
        {
            var host = ExtractHostFromPayload(row.PayloadJson);
            if (host is null) continue;
            if (granted.Contains((row.UserId, host))) continue;
            // Collapse repeat clicks from the same student on the same host —
            // a student spamming the button shouldn't inflate the per-host
            // count beyond one.
            if (!seenUserHost.Add((row.UserId, host))) continue;

            if (!byHost.TryGetValue(host, out var requesters))
            {
                requesters = new List<UnblockRequestRequester>();
                byHost[host] = requesters;
                hostFirstSeen[host] = row.OccurredAt;
            }
            requesters.Add(new UnblockRequestRequester(row.UserId, row.DisplayName, row.OccurredAt));
            hostLatest[host] = row.OccurredAt;
        }

        var summaries = byHost
            .Select(kv => new UnblockRequestSummary(
                kv.Key,
                kv.Value.Count,
                hostFirstSeen[kv.Key],
                hostLatest[kv.Key],
                kv.Value))
            .OrderByDescending(s => s.LatestRequestedAt)
            .ToList();

        return Ok(summaries);
    }

    [HttpPost("{id:guid}/unblock")]
    [Authorize(Policy = AuthorizationPolicies.Teacher)]
    [ProducesResponseType(typeof(UnblockGrantResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UnblockGrantResponse>> Unblock(
        Guid id,
        [FromBody] UnblockGrantRequest request,
        CancellationToken cancellationToken)
    {
        if (!User.TryGetEntraOid(out var entraOid))
            return Unauthorized();

        var caller = await _users.FindByEntraOidAsync(entraOid, cancellationToken);
        if (caller is null)
            return Unauthorized();

        var host = NormaliseHost(request.Host);
        if (host is null)
            return ValidationProblem("Host is required and must be a valid hostname.");

        var session = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (session is null)
            return NotFound();
        if (session.TeacherId != caller.Id)
            return Forbid();
        if (session.EndedAt is not null)
            return ValidationProblem("Session has ended.");

        var isParticipant = await _db.SessionParticipants.AsNoTracking().AnyAsync(
            p => p.SessionId == id && p.UserId == request.UserId, cancellationToken);
        if (!isParticipant)
            return ValidationProblem("Target user is not a participant of this session.");

        var now = _clock.GetUtcNow();
        var existing = await _db.SessionUnblockGrants.FirstOrDefaultAsync(
            g => g.SessionId == id && g.UserId == request.UserId && g.Host == host,
            cancellationToken);

        if (existing is null)
        {
            _db.SessionUnblockGrants.Add(new SessionUnblockGrant
            {
                SessionId = id,
                UserId = request.UserId,
                Host = host,
                GrantedAt = now,
            });
            await _db.SaveChangesAsync(cancellationToken);
        }

        // Suffix match so reddit.com also covers www.reddit.com — students
        // typically request the bare host and expect the navigation chain to
        // work. Mirrors the matcher rules (#72).
        var addedDomain = new AllowedDomainDto(AllowedDomainMatchTypes.Suffix, host);
        await _broadcaster.AllowlistAmendedAsync(
            new AllowlistAmendedPayload(id, request.UserId, new[] { addedDomain }),
            cancellationToken);

        return Ok(new UnblockGrantResponse(id, request.UserId, host, existing?.GrantedAt ?? now));
    }

    private static string? NormaliseHost(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim().ToLowerInvariant();
        // Reject anything that obviously isn't a hostname: spaces, schemes,
        // paths, ports. We accept a-z, 0-9, '-', '.'.
        foreach (var ch in trimmed)
        {
            var ok = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '.';
            if (!ok) return null;
        }
        if (trimmed.Length > 253) return null;
        return trimmed;
    }

    private static string? ExtractHostFromPayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            if (!root.TryGetProperty("host", out var h) || h.ValueKind != System.Text.Json.JsonValueKind.String)
                return null;
            var host = h.GetString()?.Trim().ToLowerInvariant();
            return string.IsNullOrEmpty(host) ? null : host;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static ObjectResult JoinByCodeError(int statusCode, string message) =>
        new(new JoinByCodeErrorResponse(message)) { StatusCode = statusCode };

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

public sealed record StartSessionRequest(Guid ClassId, IReadOnlyList<Guid>? BundleIds);

public sealed record StartSessionResponse(
    Guid Id,
    Guid ClassId,
    DateTimeOffset StartedAt,
    string JoinCode);

public sealed record EndSessionResponse(Guid Id, DateTimeOffset EndedAt);

public sealed record UpdateSessionBundlesRequest(IReadOnlyList<Guid>? BundleIds);

public sealed record UpdateSessionBundlesResponse(Guid Id, IReadOnlyList<SessionBundleSummary> Bundles);

public sealed record SessionSummary(
    Guid Id,
    Guid ClassId,
    Guid TeacherId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string JoinCode);

public sealed record SessionHistoryEntry(
    Guid Id,
    Guid ClassId,
    string ClassName,
    Guid TeacherId,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt);

public sealed record SessionDetailResponse(
    Guid Id,
    Guid ClassId,
    string ClassName,
    Guid TeacherId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string JoinCode,
    IReadOnlyList<SessionParticipantSummary> Participants,
    IReadOnlyList<SessionRecentEvent> RecentEvents,
    IReadOnlyList<SessionEventSummaryDto> Summaries,
    IReadOnlyList<SessionBundleSummary> Bundles,
    IReadOnlyList<SessionUnblockGrantDto> Grants);

public sealed record SessionBundleSummary(Guid Id, string Name);

public sealed record SessionUnblockGrantDto(
    Guid UserId,
    string DisplayName,
    string Host,
    DateTimeOffset GrantedAt);

public sealed record SessionParticipantSummary(
    Guid UserId,
    string DisplayName,
    DateTimeOffset? JoinedAt,
    DateTimeOffset? DeclinedAt,
    DateTimeOffset? LeftAt,
    string State);

public sealed record SessionRecentEvent(
    Guid Id,
    Guid UserId,
    EventKind Kind,
    string PayloadJson,
    DateTimeOffset OccurredAt);

public sealed record SessionEventSummaryDto(
    Guid UserId,
    EventKind Kind,
    int Count,
    DateTimeOffset FirstAt,
    DateTimeOffset LastAt);

public sealed record JoinByCodeRequest(string Code);

public sealed record JoinByCodeResponse(Guid SessionId);

public sealed record JoinByCodeErrorResponse(string Message);

public sealed record UnblockGrantRequest(Guid UserId, string Host);

public sealed record UnblockGrantResponse(Guid SessionId, Guid UserId, string Host, DateTimeOffset GrantedAt);

public sealed record UnblockRequestSummary(
    string Host,
    int Count,
    DateTimeOffset FirstRequestedAt,
    DateTimeOffset LatestRequestedAt,
    IReadOnlyList<UnblockRequestRequester> Requesters);

public sealed record UnblockRequestRequester(Guid UserId, string DisplayName, DateTimeOffset RequestedAt);
