using Anchor.Api.Realtime;
using Anchor.Domain.Events;
using Anchor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Anchor.Api.Sessions;

/// <summary>
/// The one way a session ends, whoever ends it: the teacher's
/// <c>POST /sessions/{id}/end</c>, or <see cref="SessionAutoEnder"/> closing a
/// session the teacher forgot (#345). Ending a session sets <c>EndedAt</c> and
/// writes its per-(student, kind) <see cref="SessionEventSummary"/> rows in one
/// transaction, drops its cached heartbeat-validation entries (#342), and
/// broadcasts <c>SessionEnded</c> — which also drops its heartbeat tracking — so
/// joined agents leave focus mode and the teacher's live page shows it ended.
/// </summary>
public sealed class SessionEnder
{
    private readonly AnchorDbContext _db;
    private readonly ISessionBroadcaster _broadcaster;
    private readonly ActiveParticipantCache _activeParticipants;
    private readonly TimeProvider _clock;

    public SessionEnder(
        AnchorDbContext db,
        ISessionBroadcaster broadcaster,
        ActiveParticipantCache activeParticipants,
        TimeProvider clock)
    {
        _db = db;
        _broadcaster = broadcaster;
        _activeParticipants = activeParticipants;
        _clock = clock;
    }

    /// <summary>
    /// Ends the session unless it has already ended, and returns its
    /// <c>EndedAt</c>. Two callers ending the same session at once — the teacher
    /// and the auto-end, or a double click — can't both write summaries: the
    /// first to mark it ended does the work, and the other gets that
    /// <c>EndedAt</c> back with <see cref="SessionEndOutcome.EndedNow"/> false.
    /// </summary>
    /// <exception cref="InvalidOperationException">The session doesn't exist.</exception>
    public async Task<SessionEndOutcome> EndAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var endedAt = _clock.GetUtcNow();

        await using (var transaction = await _db.Database.BeginTransactionAsync(cancellationToken))
        {
            // Claim the end with a conditional update rather than a read then a
            // write: only one caller can move EndedAt off null, and its row lock
            // holds off any other until the summaries below are committed.
            var claimed = await _db.Sessions
                .Where(s => s.Id == sessionId && s.EndedAt == null)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.EndedAt, endedAt), cancellationToken);

            if (claimed == 1)
            {
                await AddEventSummariesAsync(sessionId, cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                // Already ended: nothing to write, so leave the transaction to
                // roll back on dispose.
                var alreadyEndedAt = await _db.Sessions.AsNoTracking()
                    .Where(s => s.Id == sessionId)
                    .Select(s => s.EndedAt)
                    .FirstOrDefaultAsync(cancellationToken);
                return new SessionEndOutcome(
                    alreadyEndedAt ?? throw new InvalidOperationException($"Session {sessionId} does not exist."),
                    EndedNow: false);
            }
        }

        // Drop the session's cached heartbeat-validation entries (#342) so they
        // don't outlive it in memory.
        _activeParticipants.ClearSession(sessionId);
        await _broadcaster.SessionEndedAsync(sessionId, cancellationToken);
        return new SessionEndOutcome(endedAt, EndedNow: true);
    }

    /// <summary>
    /// Aggregate this session's raw events into the
    /// <see cref="SessionEventSummary"/> table so the per-(student, kind) counts
    /// survive the raw-event prune (#77). The caller's SaveChanges commits them
    /// in the same transaction as <c>EndedAt</c>.
    /// </summary>
    private async Task AddEventSummariesAsync(Guid sessionId, CancellationToken cancellationToken)
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
}

/// <summary>
/// Result of <see cref="SessionEnder.EndAsync"/>: when the session ended, and
/// whether this call ended it (false when it had already ended).
/// </summary>
public sealed record SessionEndOutcome(DateTimeOffset EndedAt, bool EndedNow);
