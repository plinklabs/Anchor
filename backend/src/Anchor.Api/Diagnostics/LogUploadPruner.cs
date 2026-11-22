using Anchor.Api.Events;
using Anchor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Anchor.Api.Diagnostics;

/// <summary>
/// Deletes the agents' log uploads (#446) once they are
/// <see cref="LogUploadOptions.RetentionDays"/> old, counted from when they
/// arrived, whether or not uploads are still switched on, and the records of
/// admins' downloads of them (#566) once those are
/// <see cref="LogAccessOptions.RetentionDays"/> old. Runs on the same
/// fixed quiet-hour grid as <see cref="EventPruner"/>
/// (<see cref="EventPruner.NextRunAfter"/>), never at startup.
/// </summary>
public sealed class LogUploadPruner : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _clock;
    private readonly IOptionsMonitor<LogUploadOptions> _options;
    private readonly IOptionsMonitor<LogAccessOptions> _accessOptions;
    private readonly IOptionsMonitor<EventRetentionOptions> _schedule;
    private readonly ILogger<LogUploadPruner> _log;

    public LogUploadPruner(
        IServiceScopeFactory scopeFactory,
        TimeProvider clock,
        IOptionsMonitor<LogUploadOptions> options,
        IOptionsMonitor<LogAccessOptions> accessOptions,
        IOptionsMonitor<EventRetentionOptions> schedule,
        ILogger<LogUploadPruner> log)
    {
        _scopeFactory = scopeFactory;
        _clock = clock;
        _options = options;
        _accessOptions = accessOptions;
        _schedule = schedule;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = _clock.GetUtcNow();
            try
            {
                await Task.Delay(EventPruner.NextRunAfter(now, _schedule.CurrentValue) - now, _clock, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Each sweep on its own: a failed one doesn't keep the other's
            // records past their retention.
            await RunAsync(PruneOnceAsync, "uploads", stoppingToken).ConfigureAwait(false);
            await RunAsync(PruneAccessRecordsOnceAsync, "access records", stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task<int>> sweep, string what, CancellationToken ct)
    {
        try
        {
            await sweep(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "LogUploadPruner run over the {What} failed", what);
        }
    }

    /// <summary>
    /// One sweep: deletes every upload that arrived before the retention
    /// cutoff and returns how many. Safe to call directly from tests.
    /// </summary>
    public async Task<int> PruneOnceAsync(CancellationToken ct)
    {
        var cutoff = _clock.GetUtcNow() - _options.CurrentValue.Retention;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();

        int deleted;
        if (db.Database.IsSqlite())
        {
            // SQLite (dev + tests) can't compare DateTimeOffset values server
            // side. The table holds two weeks of uploads, so the ids and times
            // are cheap to bring over.
            var rows = await db.SessionLogUploads.AsNoTracking()
                .Select(u => new { u.Id, u.UploadedAt })
                .ToListAsync(ct).ConfigureAwait(false);
            var expired = rows.Where(r => r.UploadedAt < cutoff).Select(r => r.Id).ToList();
            deleted = expired.Count == 0
                ? 0
                : await db.SessionLogUploads.Where(u => expired.Contains(u.Id))
                    .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
        else
        {
            deleted = await db.SessionLogUploads.Where(u => u.UploadedAt < cutoff)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        if (deleted > 0)
        {
            _log.LogInformation(
                "LogUploadPruner deleted {Deleted} log uploads that arrived before {Cutoff:o}",
                deleted, cutoff);
        }

        return deleted;
    }

    /// <summary>
    /// One sweep of the download records (#566): deletes every record of a
    /// download before <see cref="LogAccessOptions.RetentionDays"/> ago and
    /// returns how many. The only way a record is ever deleted. Safe to call
    /// directly from tests.
    /// </summary>
    public async Task<int> PruneAccessRecordsOnceAsync(CancellationToken ct)
    {
        var cutoff = _clock.GetUtcNow() - _accessOptions.CurrentValue.Retention;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();

        int deleted;
        if (db.Database.IsSqlite())
        {
            // SQLite (dev + tests) can't compare DateTimeOffset values server
            // side; a dev database holds few records.
            var rows = await db.LogAccesses.AsNoTracking()
                .Select(a => new { a.Id, a.AccessedAt })
                .ToListAsync(ct).ConfigureAwait(false);
            var expired = rows.Where(r => r.AccessedAt < cutoff).Select(r => r.Id).ToList();
            deleted = expired.Count == 0
                ? 0
                : await db.LogAccesses.Where(a => expired.Contains(a.Id))
                    .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
        else
        {
            deleted = await db.LogAccesses.Where(a => a.AccessedAt < cutoff)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        if (deleted > 0)
        {
            _log.LogInformation(
                "LogUploadPruner deleted {Deleted} log access records of downloads before {Cutoff:o}",
                deleted, cutoff);
        }

        return deleted;
    }
}
