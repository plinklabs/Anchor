using Anchor.Domain.Events;
using Anchor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Anchor.Api.Events;

/// <summary>
/// Periodically deletes raw <see cref="Event"/> rows older than the configured
/// retention window, but only when their parent session has ended. Sessions
/// without an <c>EndedAt</c> are never pruned: the per-(session, user, kind)
/// summary table is populated when a session ends (<c>SessionEnder</c>), so a
/// running session would otherwise lose its events with no aggregate to fall
/// back on. Sessions the teacher forgets to end don't stay running for long:
/// <c>SessionAutoEnder</c> ends them (#345). Deletes run in batches so a
/// one-time backlog cleanup doesn't hold a long write lock against concurrent
/// inserts.
/// <para>
/// Runs are scheduled at a fixed quiet hour, never at startup (#344): see
/// <see cref="NextRunAfter"/>.
/// </para>
/// </summary>
public sealed class EventPruner : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _clock;
    private readonly IOptionsMonitor<EventRetentionOptions> _options;
    private readonly ILogger<EventPruner> _log;

    public EventPruner(
        IServiceScopeFactory scopeFactory,
        TimeProvider clock,
        IOptionsMonitor<EventRetentionOptions> options,
        ILogger<EventPruner> log)
    {
        _scopeFactory = scopeFactory;
        _clock = clock;
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Wait first: a fresh process doesn't query the database until the
            // next scheduled run.
            var now = _clock.GetUtcNow();
            try
            {
                await Task.Delay(NextRunAfter(now, _options.CurrentValue) - now, _clock, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await PruneOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // shutdown
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "EventPruner scan failed");
            }
        }
    }

    /// <summary>
    /// The first scheduled run strictly after <paramref name="now"/>. Runs fall
    /// on a fixed UTC grid: <see cref="EventRetentionOptions.PruneHourUtc"/>,
    /// then every <see cref="EventRetentionOptions.PruneInterval"/> from there
    /// (with the default daily interval, once a day at that hour). Because the
    /// grid doesn't depend on when the process started, a restart neither runs
    /// a prune right away nor pushes the next one back — waiting a full interval
    /// after each start would never prune on days with several deploys.
    /// </summary>
    public static DateTimeOffset NextRunAfter(DateTimeOffset now, EventRetentionOptions options)
    {
        var interval = options.PruneInterval;
        var anchor = DateTimeOffset.UnixEpoch.AddHours(options.EffectivePruneHourUtc);
        var intervalsElapsed = (now - anchor).Ticks / interval.Ticks;
        return anchor + TimeSpan.FromTicks((intervalsElapsed + 1) * interval.Ticks);
    }

    /// <summary>
    /// One sweep of the prune logic. Returns the number of raw event rows
    /// deleted. Safe to call directly from tests.
    /// </summary>
    public async Task<int> PruneOnceAsync(CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        var cutoff = _clock.GetUtcNow() - opts.RawEventMaxAge;
        var batchSize = Math.Max(1, opts.BatchSize);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();

        // SQLite (dev + tests) stores DateTimeOffset as a 9-byte blob and
        // can't translate `OccurredAt < cutoff` server-side. SqlServer (prod)
        // would push that comparison down. We branch the candidate scan on
        // provider rather than always materialising — at school scale on
        // SqlServer, dumping every ended-session row into memory just to
        // filter by date would blow up. Sessions IDs themselves are bounded
        // and always materialised so the IN-list is cheap on both providers.
        var endedSessionIds = await db.Sessions.AsNoTracking()
            .Where(s => s.EndedAt != null)
            .Select(s => s.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        var activeSessionIds = await db.Sessions.AsNoTracking()
            .Where(s => s.EndedAt == null)
            .Select(s => s.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        var sqliteFallback = db.Database.IsSqlite();

        // Orphan check: rows older than the cutoff under sessions that never
        // got an EndedAt. We don't delete these — the design (#77) is
        // explicit that active sessions are protected even if they cross
        // the retention window. With forgotten sessions auto-ended (#345), a
        // non-trivial pile of them is a signal that ending failed for someone
        // (or auto-ending is turned off), so surface it.
        var orphanCount = sqliteFallback
            ? (await db.Events.AsNoTracking()
                .Where(e => activeSessionIds.Contains(e.SessionId))
                .Select(e => e.OccurredAt)
                .ToListAsync(ct).ConfigureAwait(false))
                .Count(t => t < cutoff)
            : await db.Events.AsNoTracking()
                .Where(e => e.OccurredAt < cutoff && activeSessionIds.Contains(e.SessionId))
                .CountAsync(ct).ConfigureAwait(false);
        if (orphanCount > opts.OrphanedActiveSessionWarnThreshold)
        {
            _log.LogWarning(
                "EventPruner: {OrphanCount} events older than {Cutoff:o} are under sessions with EndedAt=null. Skipped (active-session protection).",
                orphanCount, cutoff);
        }

        var totalDeleted = 0;
        while (!ct.IsCancellationRequested)
        {
            var idBatch = await FetchPruneCandidateIdsAsync(
                db, endedSessionIds, cutoff, batchSize, sqliteFallback, ct).ConfigureAwait(false);
            if (idBatch.Count == 0) break;

            var deleted = await db.Events
                .Where(e => idBatch.Contains(e.Id))
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);

            totalDeleted += deleted;
            if (idBatch.Count < batchSize) break;
        }

        if (totalDeleted > 0)
        {
            _log.LogInformation(
                "EventPruner deleted {Deleted} raw events older than {Cutoff:o}",
                totalDeleted, cutoff);
        }

        return totalDeleted;
    }

    private static async Task<List<Guid>> FetchPruneCandidateIdsAsync(
        AnchorDbContext db,
        List<Guid> endedSessionIds,
        DateTimeOffset cutoff,
        int batchSize,
        bool sqliteFallback,
        CancellationToken ct)
    {
        if (!sqliteFallback)
        {
            return await db.Events.AsNoTracking()
                .Where(e => e.OccurredAt < cutoff && endedSessionIds.Contains(e.SessionId))
                .OrderBy(e => e.OccurredAt)
                .Take(batchSize)
                .Select(e => e.Id)
                .ToListAsync(ct).ConfigureAwait(false);
        }

        // SQLite fallback: fetch a wider window keyed by Id, filter by
        // OccurredAt in memory. Dev/test volumes are small enough that this
        // is cheaper than smuggling a comparable surrogate column into the
        // schema just to make the translator happy. A single pass per
        // PruneOnceAsync call is sufficient for the test suite.
        var rows = await db.Events.AsNoTracking()
            .Where(e => endedSessionIds.Contains(e.SessionId))
            .OrderBy(e => e.Id)
            .Take(batchSize * 2)
            .Select(e => new { e.Id, e.OccurredAt })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows
            .Where(r => r.OccurredAt < cutoff)
            .Select(r => r.Id)
            .Take(batchSize)
            .ToList();
    }
}
