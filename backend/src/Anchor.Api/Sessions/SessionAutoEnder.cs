using Anchor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Anchor.Api.Sessions;

/// <summary>
/// Ends sessions the teacher forgot to end (#345). Only the teacher's End used
/// to set <c>EndedAt</c>, so a session left running when the dashboard closed
/// stayed in <c>/sessions/active</c> and <c>/sessions/rejoinable</c> for good:
/// any participant whose agent restarted later — next lesson, next day — was
/// silently put back into its focus mode (#54), and its raw events were never
/// summarised or pruned (<see cref="Events.EventPruner"/> skips sessions that
/// haven't ended).
///
/// <para>
/// Every <see cref="SessionAutoEndOptions.SweepInterval"/> it ends each session
/// that started more than <see cref="SessionAutoEndOptions.MaxDuration"/> ago,
/// through the same <see cref="SessionEnder"/> path as the teacher's End:
/// summaries, the heartbeat-validation cache, and the <c>SessionEnded</c>
/// broadcast all behave as if the teacher had clicked End.
/// </para>
///
/// <para>
/// It waits one interval before its first sweep, so a (re)start doesn't query
/// the database (#344).
/// </para>
/// </summary>
public sealed class SessionAutoEnder : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _clock;
    private readonly IOptionsMonitor<SessionAutoEndOptions> _options;
    private readonly ILogger<SessionAutoEnder> _log;

    public SessionAutoEnder(
        IServiceScopeFactory scopeFactory,
        TimeProvider clock,
        IOptionsMonitor<SessionAutoEndOptions> options,
        ILogger<SessionAutoEnder> log)
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
            try
            {
                await Task.Delay(_options.CurrentValue.EffectiveSweepInterval, _clock, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await EndForgottenSessionsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // shutdown
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "SessionAutoEnder sweep failed");
            }
        }
    }

    /// <summary>
    /// One sweep: ends every running session older than
    /// <see cref="SessionAutoEndOptions.MaxDuration"/> and returns the ids of the
    /// sessions it ended. Safe to call directly from tests.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> EndForgottenSessionsAsync(CancellationToken ct)
    {
        var maxDuration = _options.CurrentValue.MaxDuration;
        if (maxDuration <= TimeSpan.Zero)
            return Array.Empty<Guid>();

        var now = _clock.GetUtcNow();

        List<Guid> forgotten;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
            // Running sessions are few — the lessons going on right now plus any
            // forgotten ones — so their age is checked in memory: SQLite (dev +
            // tests) can't compare DateTimeOffset values server-side.
            var running = await db.Sessions.AsNoTracking()
                .Where(s => s.EndedAt == null)
                .Select(s => new { s.Id, s.StartedAt })
                .ToListAsync(ct).ConfigureAwait(false);
            forgotten = running
                .Where(s => now - s.StartedAt >= maxDuration)
                .Select(s => s.Id)
                .ToList();
        }

        var ended = new List<Guid>(forgotten.Count);
        foreach (var sessionId in forgotten)
        {
            // A scope per session: a failure leaves nothing behind in a shared
            // change tracker for the next one to trip over.
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var ender = scope.ServiceProvider.GetRequiredService<SessionEnder>();
                var outcome = await ender.EndAsync(sessionId, ct).ConfigureAwait(false);
                if (!outcome.EndedNow)
                    continue; // the teacher ended it while this sweep ran

                ended.Add(sessionId);
                _log.LogInformation(
                    "Ended forgotten session {SessionId}: still running {MaxDuration} after it started",
                    sessionId, maxDuration);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "SessionAutoEnder could not end session {SessionId}", sessionId);
            }
        }

        return ended;
    }
}
