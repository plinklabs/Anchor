using Anchor.Api.Realtime;
using Anchor.Api.Tests.FakeAuth;
using Anchor.Domain.Events;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    public sealed class MonitorTestFactory : AnchorApiFactory
    {
        /// <summary>
        /// The clock the hub records pings by, for the tests that drive the
        /// pings through it. The others give their monitor a clock of its own.
        /// </summary>
        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
            });
        }
    }

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

    [Fact]
    public async Task An_agent_that_drops_out_mid_session_is_a_lost_heartbeat_and_no_tamper_flag()
    {
        // #569: wifi, sleep, a crash, Task Manager. An agent in the field also
        // pings for its extension (ExtensionHeartbeat) while the witness link is
        // up, and both pings stop together. Its absence used to reach the
        // teacher as HeartbeatLost and a sticky extension_silent flag as well.
        var (sessionId, userId) = await SeedSessionAndStudentAsync();
        var tracker = _factory.Services.GetRequiredService<HeartbeatTracker>();
        var monitor = BuildMonitor(tracker, _factory.Clock);
        await using var agent = await ConnectAsync(userId);

        for (var ping = 0; ping < 3; ping++)
        {
            await agent.InvokeAsync(nameof(SessionHub.Heartbeat), sessionId);
            await agent.InvokeAsync(nameof(SessionHub.ExtensionHeartbeat), sessionId);
            await monitor.ScanOnceAsync(CancellationToken.None);
            _factory.Clock.Advance(TimeSpan.FromSeconds(10));
        }

        // The laptop drops off the network: nothing more arrives from it.
        for (var second = 5; second <= 30; second += 5)
        {
            _factory.Clock.Advance(TimeSpan.FromSeconds(5));
            await monitor.ScanOnceAsync(CancellationToken.None);
        }

        Assert.Equal(new[] { EventKind.HeartbeatLost }, await EventKindsAsync(sessionId, userId));
        var broadcaster = _factory.Services.GetRequiredService<RecordingSessionBroadcaster>();
        Assert.Single(broadcaster.HeartbeatLostCalls, c => c.SessionId == sessionId && c.UserId == userId);
        Assert.DoesNotContain(broadcaster.TamperDetectedCalls, c => c.SessionId == sessionId && c.UserId == userId);
    }

    [Fact]
    public async Task Closing_Edge_mid_session_raises_no_tamper_flag()
    {
        // #569: the student closes Edge, so the extension's witness link drops
        // and an agent in the field stops pinging for it, while it keeps pinging
        // for itself. The agent reports nothing for a closed Edge (#518); the
        // backend used to flag extension_silent 20 seconds later.
        var (sessionId, userId) = await SeedSessionAndStudentAsync();
        var tracker = _factory.Services.GetRequiredService<HeartbeatTracker>();
        var monitor = BuildMonitor(tracker, _factory.Clock);
        await using var agent = await ConnectAsync(userId);

        for (var second = 0; second <= 90; second += 10)
        {
            await agent.InvokeAsync(nameof(SessionHub.Heartbeat), sessionId);
            // Edge is closed 20 seconds in.
            if (second < 20)
                await agent.InvokeAsync(nameof(SessionHub.ExtensionHeartbeat), sessionId);
            await monitor.ScanOnceAsync(CancellationToken.None);
            _factory.Clock.Advance(TimeSpan.FromSeconds(5));
            await monitor.ScanOnceAsync(CancellationToken.None);
            _factory.Clock.Advance(TimeSpan.FromSeconds(5));
        }

        Assert.Empty(await EventKindsAsync(sessionId, userId));
        var broadcaster = _factory.Services.GetRequiredService<RecordingSessionBroadcaster>();
        Assert.DoesNotContain(broadcaster.TamperDetectedCalls, c => c.SessionId == sessionId && c.UserId == userId);
        Assert.DoesNotContain(broadcaster.HeartbeatLostCalls, c => c.SessionId == sessionId && c.UserId == userId);
    }

    [Fact]
    public async Task Extension_source_going_stale_reports_nothing()
    {
        // #569: the monitor no longer turns the extension's silence into an
        // extension_silent tamper flag, nor into anything else.
        var (sessionId, userId) = await SeedSessionAndStudentAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var tracker = new HeartbeatTracker();
        var monitor = BuildMonitor(tracker, clock);

        tracker.Record(sessionId, userId, clock.GetUtcNow(), WitnessSource.Extension);
        clock.Advance(TimeSpan.FromSeconds(25));
        await monitor.ScanOnceAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(2));
        tracker.Record(sessionId, userId, clock.GetUtcNow(), WitnessSource.Extension);
        await monitor.ScanOnceAsync(CancellationToken.None);

        Assert.Empty(await EventKindsAsync(sessionId, userId));
        var broadcaster = _factory.Services.GetRequiredService<RecordingSessionBroadcaster>();
        Assert.DoesNotContain(broadcaster.TamperDetectedCalls, c => c.SessionId == sessionId && c.UserId == userId);
    }

    [Fact]
    public async Task Extension_heartbeat_does_not_suppress_agent_HeartbeatLost()
    {
        // Acceptance criterion (#149): agent and extension share a userId, so a
        // fresh extension ping must not keep the agent's HeartbeatLost rule warm.
        var (sessionId, userId) = await SeedSessionAndStudentAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var tracker = new HeartbeatTracker();
        var monitor = BuildMonitor(tracker, clock);

        tracker.Record(sessionId, userId, clock.GetUtcNow(), WitnessSource.Agent);
        // The agent goes silent; the extension keeps pinging right up to the scan.
        clock.Advance(TimeSpan.FromSeconds(25));
        tracker.Record(sessionId, userId, clock.GetUtcNow(), WitnessSource.Extension);

        await monitor.ScanOnceAsync(CancellationToken.None);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        Assert.True(await db.Events.AnyAsync(e =>
            e.SessionId == sessionId && e.UserId == userId && e.Kind == EventKind.HeartbeatLost));
        // The still-fresh extension is not itself flagged.
        Assert.False(await db.Events.AnyAsync(e =>
            e.SessionId == sessionId && e.UserId == userId && e.Kind == EventKind.TamperDetected));
    }

    [Fact]
    public async Task Participant_who_is_no_longer_active_is_never_reported_and_stops_being_tracked()
    {
        // #448: leaving drops the participant's tracking, but a ping that passed
        // the hub's active check just before the leave can land after it. Its
        // silence must not reach the teacher as a lost agent or a silent
        // extension.
        var (sessionId, userId) = await SeedSessionAndStudentAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
            var participant = await db.SessionParticipants
                .SingleAsync(p => p.SessionId == sessionId && p.UserId == userId);
            participant.LeftAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            _factory.Services.GetRequiredService<ActiveParticipantCache>().Update(participant);
        }

        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var tracker = new HeartbeatTracker();
        var monitor = BuildMonitor(tracker, clock);
        tracker.Record(sessionId, userId, clock.GetUtcNow(), WitnessSource.Agent);
        tracker.Record(sessionId, userId, clock.GetUtcNow(), WitnessSource.Extension);

        clock.Advance(TimeSpan.FromSeconds(25));
        await monitor.ScanOnceAsync(CancellationToken.None);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
            Assert.False(await db.Events.AnyAsync(e => e.SessionId == sessionId && e.UserId == userId));
        }
        var broadcaster = _factory.Services.GetRequiredService<RecordingSessionBroadcaster>();
        Assert.DoesNotContain(broadcaster.HeartbeatLostCalls, c => c.SessionId == sessionId && c.UserId == userId);
        Assert.DoesNotContain(broadcaster.TamperDetectedCalls, c => c.SessionId == sessionId && c.UserId == userId);
        Assert.False(tracker.TryGet(sessionId, userId, out _, WitnessSource.Agent));
        Assert.False(tracker.TryGet(sessionId, userId, out _, WitnessSource.Extension));
    }

    [Fact]
    public async Task Agent_that_never_pings_after_the_join_is_reported_missing_once_the_grace_passes()
    {
        // #444: only the extension joined. Its join starts the wait for the
        // agent, which never sends a heartbeat.
        var (sessionId, userId) = await SeedSessionAndStudentAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var tracker = new HeartbeatTracker();
        var monitor = BuildMonitor(tracker, clock);

        tracker.ExpectAgent(sessionId, userId, clock.GetUtcNow());

        // 25s: past the 20s heartbeat timeout, still inside the 30s join grace.
        clock.Advance(TimeSpan.FromSeconds(25));
        await monitor.ScanOnceAsync(CancellationToken.None);
        Assert.Empty(await EventKindsAsync(sessionId, userId));

        // 31s: past the grace. One report per absence, not one per scan.
        clock.Advance(TimeSpan.FromSeconds(6));
        await monitor.ScanOnceAsync(CancellationToken.None);
        await monitor.ScanOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { EventKind.AgentMissing }, await EventKindsAsync(sessionId, userId));
        var broadcaster = _factory.Services.GetRequiredService<RecordingSessionBroadcaster>();
        var pushed = Assert.Single(broadcaster.ParticipantStateChangedCalls, c => c.SessionId == sessionId && c.UserId == userId);
        Assert.Equal(nameof(ParticipantLiveState.AgentMissing), pushed.State);
        Assert.Equal("Student", pushed.DisplayName);
        // An agent that was never there is not a lost one.
        Assert.DoesNotContain(broadcaster.HeartbeatLostCalls, c => c.SessionId == sessionId && c.UserId == userId);
    }

    [Fact]
    public async Task Agent_that_turns_up_late_clears_the_report_and_is_then_watched_like_any_agent()
    {
        var (sessionId, userId) = await SeedSessionAndStudentAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var tracker = new HeartbeatTracker();
        var monitor = BuildMonitor(tracker, clock);

        tracker.ExpectAgent(sessionId, userId, clock.GetUtcNow());
        clock.Advance(TimeSpan.FromSeconds(31));
        await monitor.ScanOnceAsync(CancellationToken.None); // AgentMissing

        // The agent starts after all and pings.
        clock.Advance(TimeSpan.FromSeconds(5));
        tracker.Record(sessionId, userId, clock.GetUtcNow());
        await monitor.ScanOnceAsync(CancellationToken.None); // AgentReconnected

        // Now it has been there, going silent is a lost heartbeat, after the
        // usual timeout rather than the join grace.
        clock.Advance(TimeSpan.FromSeconds(21));
        await monitor.ScanOnceAsync(CancellationToken.None); // HeartbeatLost

        Assert.Equal(
            new[] { EventKind.AgentMissing, EventKind.AgentReconnected, EventKind.HeartbeatLost },
            await EventKindsAsync(sessionId, userId));
    }

    [Fact]
    public async Task Agent_that_pings_within_the_grace_is_never_reported_missing()
    {
        // The usual join: the agent's toast (5s), then its first ping one
        // interval (10s) after it joined.
        var (sessionId, userId) = await SeedSessionAndStudentAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var tracker = new HeartbeatTracker();
        var monitor = BuildMonitor(tracker, clock);

        tracker.ExpectAgent(sessionId, userId, clock.GetUtcNow());
        for (var second = 5; second <= 120; second += 5)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            if (second >= 15 && second % 10 == 5)
                tracker.Record(sessionId, userId, clock.GetUtcNow());
            await monitor.ScanOnceAsync(CancellationToken.None);
        }

        Assert.Empty(await EventKindsAsync(sessionId, userId));
    }

    [Fact]
    public async Task Expecting_the_agent_again_neither_restarts_the_wait_nor_hides_a_lost_agent()
    {
        // The extension's pings expect the agent as well (after a backend
        // restart nothing else would). Neither may move an earlier start of the
        // wait, nor turn an agent that has pinged back into an awaited one.
        var (sessionId, awaited) = await SeedSessionAndStudentAsync();
        var pinged = await SeedExtraParticipantAsync(sessionId, "Pinged Student");
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var tracker = new HeartbeatTracker();
        var monitor = BuildMonitor(tracker, clock);

        tracker.ExpectAgent(sessionId, awaited, clock.GetUtcNow());
        tracker.Record(sessionId, pinged, clock.GetUtcNow());
        for (var second = 10; second <= 30; second += 10)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            tracker.ExpectAgent(sessionId, awaited, clock.GetUtcNow());
            tracker.ExpectAgent(sessionId, pinged, clock.GetUtcNow());
        }
        clock.Advance(TimeSpan.FromSeconds(1));
        await monitor.ScanOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { EventKind.AgentMissing }, await EventKindsAsync(sessionId, awaited));
        Assert.Equal(new[] { EventKind.HeartbeatLost }, await EventKindsAsync(sessionId, pinged));
    }

    private async Task<EventKind[]> EventKindsAsync(Guid sessionId, Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        return (await db.Events.AsNoTracking()
                .Where(e => e.SessionId == sessionId && e.UserId == userId)
                .Select(e => new { e.Kind, e.OccurredAt })
                .ToListAsync())
            .OrderBy(e => e.OccurredAt)
            .Select(e => e.Kind)
            .ToArray();
    }

    /// <summary>The student's agent, connected to the hub as it signs in.</summary>
    private async Task<HubConnection> ConnectAsync(Guid userId)
    {
        Guid oid;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
            oid = await db.Users.Where(u => u.Id == userId).Select(u => u.EntraOid).SingleAsync();
        }
        var server = _factory.Server;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, SessionHub.Path.TrimStart('/')), options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers[FakeJwtBearerHandler.HeaderOid] = oid.ToString();
                options.Headers[FakeJwtBearerHandler.HeaderRole] = "Student";
                options.Headers[FakeJwtBearerHandler.HeaderName] = "Student";
            })
            .Build();
        await connection.StartAsync();
        return connection;
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
            AgentJoinGraceSeconds = 30,
        });
        var activeParticipants = _factory.Services.GetRequiredService<ActiveParticipantCache>();
        return new HeartbeatMonitor(
            tracker, activeParticipants, broadcaster, scopeFactory, clock, options, NullLogger<HeartbeatMonitor>.Instance);
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
