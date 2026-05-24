using Anchor.Api.Realtime;
using Anchor.Domain.Events;
using Anchor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Anchor.Api.Tests;

/// <summary>
/// Exercises <see cref="HeartbeatMonitor.ScanOnceAsync"/> with a fake clock so
/// stale-detection logic is deterministic. The background scan loop itself is
/// disabled in tests (Heartbeat:EnableMonitor=false on <see cref="AnchorApiFactory"/>)
/// so behaviour can be observed one tick at a time.
/// </summary>
public sealed class HeartbeatMonitorTests : IClassFixture<HeartbeatMonitorTests.MonitorTestFactory>
{
    private readonly MonitorTestFactory _factory;

    public HeartbeatMonitorTests(MonitorTestFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Dedicated factory so this class's seed data and singleton
    /// <see cref="HeartbeatTracker"/> state are isolated from any other test
    /// class that also constructs an <see cref="AnchorApiFactory"/>. Sharing
    /// the type makes xUnit's default IClassFixture-per-class collide across
    /// classes that build separate factory instances but happen to race the
    /// same in-memory SQLite connection on the same Program entrypoint.
    /// </summary>
    public sealed class MonitorTestFactory : AnchorApiFactory { }

    [Fact]
    public async Task Stale_participant_emits_exactly_one_HeartbeatLost_per_outage()
    {
        var (sessionId, userId) = await SeedSessionAndStudentAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var tracker = new HeartbeatTracker();
        var monitor = BuildMonitor(tracker, clock);

        tracker.Record(sessionId, userId, clock.GetUtcNow());

        // 25s later: 2.5× the default 10s interval — past the 20s timeout.
        clock.Advance(TimeSpan.FromSeconds(25));
        await monitor.ScanOnceAsync(CancellationToken.None);
        await monitor.ScanOnceAsync(CancellationToken.None);
        await monitor.ScanOnceAsync(CancellationToken.None);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var lost = await db.Events.AsNoTracking()
            .Where(e => e.SessionId == sessionId && e.UserId == userId && e.Kind == EventKind.HeartbeatLost)
            .ToListAsync();
        Assert.Single(lost);
    }

    [Fact]
    public async Task Fresh_participant_within_timeout_does_not_emit_HeartbeatLost()
    {
        var (sessionId, userId) = await SeedSessionAndStudentAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var tracker = new HeartbeatTracker();
        var monitor = BuildMonitor(tracker, clock);

        tracker.Record(sessionId, userId, clock.GetUtcNow());

        // 15s ≤ 20s timeout — still healthy.
        clock.Advance(TimeSpan.FromSeconds(15));
        await monitor.ScanOnceAsync(CancellationToken.None);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        Assert.False(await db.Events.AnyAsync(
            e => e.SessionId == sessionId && e.UserId == userId && e.Kind == EventKind.HeartbeatLost));
    }

    [Fact]
    public async Task Resumed_heartbeat_after_outage_emits_AgentReconnected_event()
    {
        var (sessionId, userId) = await SeedSessionAndStudentAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var tracker = new HeartbeatTracker();
        var monitor = BuildMonitor(tracker, clock);

        tracker.Record(sessionId, userId, clock.GetUtcNow());

        clock.Advance(TimeSpan.FromSeconds(25));
        await monitor.ScanOnceAsync(CancellationToken.None);

        // Agent comes back; a fresh ping resets the lastSeenAt.
        clock.Advance(TimeSpan.FromSeconds(2));
        tracker.Record(sessionId, userId, clock.GetUtcNow());
        await monitor.ScanOnceAsync(CancellationToken.None);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var events = (await db.Events.AsNoTracking()
            .Where(e => e.SessionId == sessionId && e.UserId == userId)
            .ToListAsync())
            .OrderBy(e => e.OccurredAt)
            .ToList();
        Assert.Equal(2, events.Count);
        Assert.Equal(EventKind.HeartbeatLost, events[0].Kind);
        Assert.Equal(EventKind.AgentReconnected, events[1].Kind);
    }

    [Fact]
    public async Task One_stale_participant_in_session_does_not_mark_others_offline()
    {
        var (sessionId, stale) = await SeedSessionAndStudentAsync();
        var healthy = await SeedExtraParticipantAsync(sessionId, "Healthy Student");

        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var tracker = new HeartbeatTracker();
        var monitor = BuildMonitor(tracker, clock);

        tracker.Record(sessionId, stale, clock.GetUtcNow());
        // Healthy student keeps pinging right up to the scan window.
        clock.Advance(TimeSpan.FromSeconds(25));
        tracker.Record(sessionId, healthy, clock.GetUtcNow());

        await monitor.ScanOnceAsync(CancellationToken.None);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        Assert.True(await db.Events.AnyAsync(
            e => e.SessionId == sessionId && e.UserId == stale && e.Kind == EventKind.HeartbeatLost));
        Assert.False(await db.Events.AnyAsync(
            e => e.SessionId == sessionId && e.UserId == healthy && e.Kind == EventKind.HeartbeatLost));
    }

    [Fact]
    public async Task Empty_tracker_runs_no_db_queries_and_produces_no_events()
    {
        var (sessionId, userId) = await SeedSessionAndStudentAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var tracker = new HeartbeatTracker();
        var monitor = BuildMonitor(tracker, clock);

        clock.Advance(TimeSpan.FromMinutes(1));
        await monitor.ScanOnceAsync(CancellationToken.None);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        Assert.False(await db.Events.AnyAsync(e => e.SessionId == sessionId && e.UserId == userId));
    }

    private HeartbeatMonitor BuildMonitor(HeartbeatTracker tracker, FakeTimeProvider clock)
    {
        var broadcaster = _factory.Services.GetRequiredService<ISessionBroadcaster>();
        var scopeFactory = _factory.Services.GetRequiredService<IServiceScopeFactory>();
        var options = new TestOptionsMonitor(new HeartbeatOptions
        {
            IntervalSeconds = 10,
            TimeoutMultiplier = 2,
            ScanIntervalSeconds = 5,
        });
        return new HeartbeatMonitor(tracker, broadcaster, scopeFactory, clock, options, NullLogger<HeartbeatMonitor>.Instance);
    }

    private async Task<(Guid SessionId, Guid UserId)> SeedSessionAndStudentAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var teacher = new Domain.Users.User
        {
            EntraOid = Guid.NewGuid(),
            DisplayName = "Teacher",
            Role = Domain.Users.UserRole.Teacher,
        };
        var student = new Domain.Users.User
        {
            EntraOid = Guid.NewGuid(),
            DisplayName = "Student",
            Role = Domain.Users.UserRole.Student,
        };
        var @class = new Domain.Classes.Class { Name = $"C-{Guid.NewGuid():N}", SchoolYear = "2025-2026" };
        var session = new Domain.Sessions.Session
        {
            TeacherId = teacher.Id,
            ClassId = @class.Id,
            Mode = Domain.Sessions.SessionMode.Strict,
            StartedAt = DateTimeOffset.UtcNow,
            JoinCode = Random.Shared.Next(0, 1_000_000).ToString("D6"),
        };
        var participant = new Domain.Sessions.SessionParticipant
        {
            SessionId = session.Id,
            UserId = student.Id,
            JoinedAt = DateTimeOffset.UtcNow,
        };
        db.Users.AddRange(teacher, student);
        db.Classes.Add(@class);
        db.Sessions.Add(session);
        db.SessionParticipants.Add(participant);
        await db.SaveChangesAsync();
        return (session.Id, student.Id);
    }

    private async Task<Guid> SeedExtraParticipantAsync(Guid sessionId, string displayName)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var user = new Domain.Users.User
        {
            EntraOid = Guid.NewGuid(),
            DisplayName = displayName,
            Role = Domain.Users.UserRole.Student,
        };
        db.Users.Add(user);
        db.SessionParticipants.Add(new Domain.Sessions.SessionParticipant
        {
            SessionId = sessionId,
            UserId = user.Id,
            JoinedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return user.Id;
    }

    private sealed class TestOptionsMonitor : IOptionsMonitor<HeartbeatOptions>
    {
        public TestOptionsMonitor(HeartbeatOptions value) { CurrentValue = value; }
        public HeartbeatOptions CurrentValue { get; }
        public HeartbeatOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<HeartbeatOptions, string?> listener) => null;
    }
}
