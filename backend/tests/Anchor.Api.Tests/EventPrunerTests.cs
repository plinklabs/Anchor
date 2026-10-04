using Anchor.Api.Events;
using Anchor.Domain.Events;
using Anchor.Domain.Sessions;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Anchor.Api.Tests;

/// <summary>
/// Exercises <see cref="EventPruner.PruneOnceAsync"/> directly with a fake
/// clock. The host's background loop is disabled in tests
/// (EventRetention:EnablePruner=false on <see cref="AnchorApiFactory"/>) so
/// the prune scan never races the in-memory SQLite connection that the
/// test factory shares across scopes; the scheduling test starts its own
/// pruner on a fake clock instead.
/// </summary>
public sealed class EventPrunerTests : IClassFixture<EventPrunerTests.PrunerTestFactory>
{
    private readonly PrunerTestFactory _factory;

    public EventPrunerTests(PrunerTestFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Sibling pattern to <see cref="HeartbeatMonitorTests.MonitorTestFactory"/> —
    /// isolates this class's seed data from any other test class that also
    /// uses <see cref="AnchorApiFactory"/>.
    /// </summary>
    public sealed class PrunerTestFactory : AnchorApiFactory { }

    [Fact]
    public async Task Deletes_events_older_than_retention_under_ended_sessions()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        var (session, userId) = await SeedEndedSessionWithStudentAsync(endedAt: clock.GetUtcNow().AddDays(-31));

        // 35 days old, under an ended session — eligible.
        await SeedEventAsync(session, userId, EventKind.ForegroundChange, clock.GetUtcNow().AddDays(-35));
        // 5 days old, under the same ended session — too fresh to prune.
        await SeedEventAsync(session, userId, EventKind.ForegroundChange, clock.GetUtcNow().AddDays(-5));

        var pruner = BuildPruner(clock);
        var deleted = await pruner.PruneOnceAsync(CancellationToken.None);

        Assert.Equal(1, deleted);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var remaining = await db.Events.AsNoTracking()
            .Where(e => e.SessionId == session).ToListAsync();
        Assert.Single(remaining);
        Assert.True(remaining[0].OccurredAt > clock.GetUtcNow().AddDays(-10));
    }

    [Fact]
    public async Task Never_deletes_events_under_active_sessions_even_when_old()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        var (session, userId) = await SeedActiveSessionWithStudentAsync();

        // Two events under an active session, both older than 30 days.
        // Acceptance criterion: active-session events are never pruned, even
        // if the session has lingered past the retention window.
        await SeedEventAsync(session, userId, EventKind.ForegroundChange, clock.GetUtcNow().AddDays(-40));
        await SeedEventAsync(session, userId, EventKind.BlockedUrl, clock.GetUtcNow().AddDays(-45));

        var pruner = BuildPruner(clock);
        var deleted = await pruner.PruneOnceAsync(CancellationToken.None);

        Assert.Equal(0, deleted);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        Assert.Equal(2, await db.Events.CountAsync(e => e.SessionId == session));
    }

    [Fact]
    public async Task Recent_events_are_kept_even_under_ended_sessions()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        var (session, userId) = await SeedEndedSessionWithStudentAsync(endedAt: clock.GetUtcNow().AddDays(-3));

        // Session is ended but the events are well within the retention window.
        await SeedEventAsync(session, userId, EventKind.ForegroundChange, clock.GetUtcNow().AddDays(-3));
        await SeedEventAsync(session, userId, EventKind.ForegroundChange, clock.GetUtcNow().AddDays(-1));

        var pruner = BuildPruner(clock);
        var deleted = await pruner.PruneOnceAsync(CancellationToken.None);

        Assert.Equal(0, deleted);
    }

    [Fact]
    public async Task Batched_delete_processes_more_rows_than_one_batch_in_a_single_PruneOnce()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        var (session, userId) = await SeedEndedSessionWithStudentAsync(endedAt: clock.GetUtcNow().AddDays(-31));

        // 50 ancient events under an ended session, batch size 7 — pruner
        // must loop until empty (50 / 7 = 8 full batches + 1).
        await SeedBulkEventsAsync(session, userId, count: 50, occurredAt: clock.GetUtcNow().AddDays(-40));

        var pruner = BuildPruner(clock, batchSize: 7);
        var deleted = await pruner.PruneOnceAsync(CancellationToken.None);

        Assert.Equal(50, deleted);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        Assert.Equal(0, await db.Events.CountAsync(e => e.SessionId == session));
    }

    [Fact]
    public async Task Concurrent_inserts_during_prune_are_preserved_and_recent_rows_survive()
    {
        // The acceptance criterion: "batched delete completes without locking
        // out concurrent inserts". With the shared in-memory SQLite connection
        // the test factory uses, the test verifies the final state — old rows
        // gone, new rows kept — and that the prune actually completes (no
        // deadlock or timeout).
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        var (session, userId) = await SeedEndedSessionWithStudentAsync(endedAt: clock.GetUtcNow().AddDays(-31));
        var (activeSession, activeUserId) = await SeedActiveSessionWithStudentAsync();

        await SeedBulkEventsAsync(session, userId, count: 40, occurredAt: clock.GetUtcNow().AddDays(-40));

        var pruner = BuildPruner(clock, batchSize: 5);
        var pruneTask = pruner.PruneOnceAsync(CancellationToken.None);
        // Insert 5 fresh events under an active session while the prune is
        // running. SQLite shared-cache writes serialise, so this exercises
        // the "writers can make progress between batches" assertion.
        await SeedBulkEventsAsync(activeSession, activeUserId, count: 5, occurredAt: clock.GetUtcNow());
        var deleted = await pruneTask;

        Assert.Equal(40, deleted);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        Assert.Equal(0, await db.Events.CountAsync(e => e.SessionId == session));
        Assert.Equal(5, await db.Events.CountAsync(e => e.SessionId == activeSession));
    }

    [Theory]
    // Before today's run hour: later the same day.
    [InlineData("2026-03-01T01:30:00Z", 1440, 2, "2026-03-01T02:00:00Z")]
    // After it (a deploy mid-morning): tomorrow at the hour, not now.
    [InlineData("2026-03-01T10:15:00Z", 1440, 2, "2026-03-02T02:00:00Z")]
    // Exactly on a run: strictly after, so a finished run isn't repeated.
    [InlineData("2026-03-01T02:00:00Z", 1440, 2, "2026-03-02T02:00:00Z")]
    // A shorter interval stays on the grid anchored at the hour.
    [InlineData("2026-03-01T10:15:00Z", 60, 2, "2026-03-01T11:00:00Z")]
    // Out-of-range settings degrade instead of throwing: hour wraps, interval >= 1 min.
    [InlineData("2026-03-01T10:15:00Z", 1440, 26, "2026-03-02T02:00:00Z")]
    [InlineData("2026-03-01T10:15:30Z", 0, 2, "2026-03-01T10:16:00Z")]
    public void NextRunAfter_follows_the_fixed_utc_schedule(
        string now, int intervalMinutes, int hourUtc, string expected)
    {
        var options = new EventRetentionOptions
        {
            PruneIntervalMinutes = intervalMinutes,
            PruneHourUtc = hourUtc,
        };

        var next = EventPruner.NextRunAfter(DateTimeOffset.Parse(now), options);

        Assert.Equal(DateTimeOffset.Parse(expected), next);
    }

    [Fact]
    public async Task Background_loop_waits_for_the_run_hour_instead_of_pruning_at_startup()
    {
        // #344: the pruner used to prune as soon as the app started, so every
        // (re)start queried the database before serving anything. Started at
        // 12:00 UTC with the run hour at 02:00, its first prune is 14 h away.
        var clock = new TimerRecordingTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        var (session, userId) = await SeedEndedSessionWithStudentAsync(endedAt: clock.GetUtcNow().AddDays(-31));
        await SeedEventAsync(session, userId, EventKind.ForegroundChange, clock.GetUtcNow().AddDays(-35));
        var timeout = TimeSpan.FromSeconds(10);

        var pruner = BuildPruner(clock, pruneHourUtc: 2);
        await pruner.StartAsync(CancellationToken.None);
        try
        {
            // Parked on its first wait (the only timer this pruner creates),
            // with nothing pruned on the way there, until 02:00.
            var firstWait = await clock.WaitForTimerAsync(_ => true, timeout);
            Assert.Equal(1, await CountEventsAsync(session));
            Assert.Equal(TimeSpan.FromHours(14), firstWait);

            clock.Advance(firstWait - TimeSpan.FromSeconds(1));
            Assert.Equal(1, await CountEventsAsync(session));

            // 02:00 UTC: the scheduled run prunes, then waits for tomorrow's.
            clock.Advance(TimeSpan.FromSeconds(1));
            await clock.WaitForTimerAsync(due => due == TimeSpan.FromHours(24), timeout);
            Assert.Equal(0, await CountEventsAsync(session));
        }
        finally
        {
            await pruner.StopAsync(CancellationToken.None);
        }
    }

    private async Task<int> CountEventsAsync(Guid sessionId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        return await db.Events.CountAsync(e => e.SessionId == sessionId);
    }

    private EventPruner BuildPruner(TimeProvider clock, int batchSize = 10_000, int pruneHourUtc = 2)
    {
        var scopeFactory = _factory.Services.GetRequiredService<IServiceScopeFactory>();
        var options = new TestOptionsMonitor(new EventRetentionOptions
        {
            RawEventDays = 30,
            PruneIntervalMinutes = 1440,
            PruneHourUtc = pruneHourUtc,
            BatchSize = batchSize,
            EnablePruner = false,
        });
        return new EventPruner(scopeFactory, clock, options, NullLogger<EventPruner>.Instance);
    }

    private async Task<(Guid SessionId, Guid UserId)> SeedEndedSessionWithStudentAsync(DateTimeOffset endedAt)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var (teacher, student, @class) = SeedRoster();
        var session = new Session
        {
            TeacherId = teacher.Id,
            ClassId = @class.Id,
            StartedAt = endedAt.AddHours(-1),
            EndedAt = endedAt,
            JoinCode = Random.Shared.Next(0, 1_000_000).ToString("D6"),
        };
        db.Users.AddRange(teacher, student);
        db.Classes.Add(@class);
        db.Sessions.Add(session);
        db.SessionParticipants.Add(new SessionParticipant { SessionId = session.Id, UserId = student.Id });
        await db.SaveChangesAsync();
        return (session.Id, student.Id);
    }

    private async Task<(Guid SessionId, Guid UserId)> SeedActiveSessionWithStudentAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var (teacher, student, @class) = SeedRoster();
        var session = new Session
        {
            TeacherId = teacher.Id,
            ClassId = @class.Id,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            JoinCode = Random.Shared.Next(0, 1_000_000).ToString("D6"),
        };
        db.Users.AddRange(teacher, student);
        db.Classes.Add(@class);
        db.Sessions.Add(session);
        db.SessionParticipants.Add(new SessionParticipant { SessionId = session.Id, UserId = student.Id });
        await db.SaveChangesAsync();
        return (session.Id, student.Id);
    }

    private static (User Teacher, User Student, Anchor.Domain.Classes.Class Class) SeedRoster() => (
        new User
        {
            EntraOid = Guid.NewGuid(),
            DisplayName = "Teacher " + Guid.NewGuid().ToString("N").Substring(0, 6),
            Role = UserRole.Teacher,
        },
        new User
        {
            EntraOid = Guid.NewGuid(),
            DisplayName = "Student " + Guid.NewGuid().ToString("N").Substring(0, 6),
            Role = UserRole.Student,
        },
        new Anchor.Domain.Classes.Class
        {
            Name = "C-" + Guid.NewGuid().ToString("N").Substring(0, 6),
            SchoolYear = "2025-2026",
        });

    private async Task SeedEventAsync(Guid sessionId, Guid userId, EventKind kind, DateTimeOffset occurredAt)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        db.Events.Add(new Event
        {
            SessionId = sessionId,
            UserId = userId,
            Kind = kind,
            PayloadJson = "{}",
            OccurredAt = occurredAt,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedBulkEventsAsync(Guid sessionId, Guid userId, int count, DateTimeOffset occurredAt)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        for (var i = 0; i < count; i++)
        {
            db.Events.Add(new Event
            {
                SessionId = sessionId,
                UserId = userId,
                Kind = EventKind.ForegroundChange,
                PayloadJson = "{}",
                OccurredAt = occurredAt.AddSeconds(i),
            });
        }
        await db.SaveChangesAsync();
    }

    private sealed class TestOptionsMonitor : IOptionsMonitor<EventRetentionOptions>
    {
        public TestOptionsMonitor(EventRetentionOptions value) { CurrentValue = value; }
        public EventRetentionOptions CurrentValue { get; }
        public EventRetentionOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<EventRetentionOptions, string?> listener) => null;
    }
}
