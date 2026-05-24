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
/// clock. The background loop is disabled in tests
/// (EventRetention:EnablePruner=false on <see cref="AnchorApiFactory"/>) so
/// the prune scan never races the in-memory SQLite connection that the
/// test factory shares across scopes.
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

    private EventPruner BuildPruner(FakeTimeProvider clock, int batchSize = 10_000)
    {
        var scopeFactory = _factory.Services.GetRequiredService<IServiceScopeFactory>();
        var options = new TestOptionsMonitor(new EventRetentionOptions
        {
            RawEventDays = 30,
            PruneIntervalMinutes = 1440,
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
            Mode = SessionMode.Strict,
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
            Mode = SessionMode.Strict,
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
