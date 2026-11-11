using Anchor.Domain.Sessions;
using Anchor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Anchor.Api.Sessions;

/// <summary>
/// Ends sessions on their own: each at its planned end (#447), and any the
/// teacher forgot (#345). Only the teacher's End used to set <c>EndedAt</c>, so
/// a session left running when the dashboard closed stayed in
/// <c>/sessions/active</c> and <c>/sessions/rejoinable</c> for good: any
/// participant whose agent restarted later — next lesson, next day — was
/// silently put back into its focus mode (#54), and its raw events were never
/// summarised or pruned (<see cref="Events.EventPruner"/> skips sessions that
/// haven't ended).
///
/// <para>
/// A session started with a planned end (<c>Session.EndsAt</c>) ends then: the
/// <see cref="SessionEndSchedule"/> wakes this service at the earliest planned
/// end it holds, and a newly scheduled one that comes sooner wakes it early.
/// Every <see cref="SessionAutoEndOptions.SweepInterval"/> a sweep also ends
/// each session past its planned end that the schedule didn't hold (the
/// backend restarted since it started), schedules the planned ends still to
/// come, and ends each session that started more than
/// <see cref="SessionAutoEndOptions.MaxDuration"/> ago, the last safety net.
/// Either way it ends them through the same <see cref="SessionEnder"/> path as
/// the teacher's End: summaries, the heartbeat-validation cache, and the
/// <c>SessionEnded</c> broadcast all behave as if the teacher had clicked End.
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
    private readonly SessionEndSchedule _schedule;
    private readonly ILogger<SessionAutoEnder> _log;

    public SessionAutoEnder(
        IServiceScopeFactory scopeFactory,
        TimeProvider clock,
        IOptionsMonitor<SessionAutoEndOptions> options,
        SessionEndSchedule schedule,
        ILogger<SessionAutoEnder> log)
    {
        _scopeFactory = scopeFactory;
        _clock = clock;
        _options = options;
        _schedule = schedule;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextSweep = _clock.GetUtcNow() + _options.CurrentValue.EffectiveSweepInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await WaitAsync(nextSweep, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                if (_clock.GetUtcNow() >= nextSweep)
                {
                    nextSweep = _clock.GetUtcNow() + _options.CurrentValue.EffectiveSweepInterval;
                    await EndForgottenSessionsAsync(stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    await EndSessionsAtTheirPlannedEndAsync(stoppingToken).ConfigureAwait(false);
                }
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
    /// Waits until <paramref name="nextSweep"/> or the schedule's earliest
    /// planned end, whichever comes first, or until a session is scheduled,
    /// whose planned end may come sooner.
    /// </summary>
    private async Task WaitAsync(DateTimeOffset nextSweep, CancellationToken stoppingToken)
    {
        var scheduled = _schedule.WhenChanged;
        var wakeAt = _schedule.Earliest is { } plannedEnd && plannedEnd < nextSweep ? plannedEnd : nextSweep;
        var delay = wakeAt - _clock.GetUtcNow();
        if (delay > TimeSpan.Zero)
        {
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            await Task.WhenAny(Task.Delay(delay, _clock, waiting.Token), scheduled).ConfigureAwait(false);
            // Woken by the schedule: drop the timer rather than leave it running.
            await waiting.CancelAsync().ConfigureAwait(false);
        }
        stoppingToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Ends every scheduled session whose planned end has come, and returns
    /// the ids of the sessions it ended. Touches the database only for those.
    /// Safe to call directly from tests.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> EndSessionsAtTheirPlannedEndAsync(CancellationToken ct)
    {
        var ended = new List<Guid>();
        foreach (var sessionId in _schedule.TakeDue(_clock.GetUtcNow()))
        {
            if (await TryEndAsync(sessionId, "it reached its planned end", ct).ConfigureAwait(false))
                ended.Add(sessionId);
        }
        return ended;
    }

    /// <summary>
    /// One sweep: ends every running session past its planned end or older
    /// than <see cref="SessionAutoEndOptions.MaxDuration"/>, schedules the
    /// planned ends of the others, and returns the ids of the sessions it
    /// ended. Safe to call directly from tests.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> EndForgottenSessionsAsync(CancellationToken ct)
    {
        var maxDuration = _options.CurrentValue.MaxDuration;
        var now = _clock.GetUtcNow();

        var due = new List<(Guid SessionId, string Reason)>();
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
            // Running sessions are few — the lessons going on right now plus any
            // forgotten ones — so their times are checked in memory: SQLite (dev +
            // tests) can't compare DateTimeOffset values server-side.
            var running = await db.Sessions.AsNoTracking()
                .Where(s => s.EndedAt == null)
                .Select(s => new { s.Id, s.StartedAt, s.EndsAt })
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var session in running)
            {
                if (Session.IsPastPlannedEnd(session.EndsAt, now))
                    due.Add((session.Id, "it passed its planned end"));
                else if (maxDuration > TimeSpan.Zero && now - session.StartedAt >= maxDuration)
                    due.Add((session.Id, $"it was still running {maxDuration} after it started"));
                else if (session.EndsAt is { } endsAt)
                    _schedule.Schedule(session.Id, endsAt);
            }
        }

        var ended = new List<Guid>(due.Count);
        foreach (var (sessionId, reason) in due)
        {
            if (await TryEndAsync(sessionId, reason, ct).ConfigureAwait(false))
                ended.Add(sessionId);
        }

        return ended;
    }

    /// <summary>
    /// Ends one session, in a scope of its own: a failure leaves nothing behind
    /// in a shared change tracker for the next one to trip over. False when it
    /// had already ended (the teacher ended it while this ran) or ending failed.
    /// </summary>
    private async Task<bool> TryEndAsync(Guid sessionId, string reason, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var ender = scope.ServiceProvider.GetRequiredService<SessionEnder>();
            var outcome = await ender.EndAsync(sessionId, ct).ConfigureAwait(false);
            if (!outcome.EndedNow)
                return false;

            _log.LogInformation("Ended session {SessionId}: {Reason}", sessionId, reason);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "SessionAutoEnder could not end session {SessionId}", sessionId);
            return false;
        }
    }
}
