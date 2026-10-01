using System.Net;
using System.Net.Http.Json;
using Anchor.Api.Controllers;
using Anchor.Api.Realtime;
using Anchor.Api.Sessions;
using Anchor.Api.Tests.FakeAuth;
using Anchor.Domain.Events;
using Anchor.Domain.Sessions;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Anchor.Api.Tests;

/// <summary>
/// Forgotten sessions end on their own (#345). Each test builds its own
/// <see cref="SessionAutoEnder"/> on a fake clock and drives
/// <see cref="SessionAutoEnder.EndForgottenSessionsAsync"/> (or its background
/// loop) directly. The instance the host registers runs on the real clock and
/// waits five minutes before its first sweep, so it never sweeps while these
/// tests run — they don't rely on <c>SessionAutoEnd:EnableAutoEnder=false</c>
/// in <see cref="AnchorApiFactory"/>, which doesn't reach Program.cs yet (#353).
/// </summary>
public sealed class SessionAutoEnderTests : IClassFixture<SessionAutoEnderTests.AutoEndTestFactory>
{
    private static readonly TimeSpan MaxDuration = TimeSpan.FromHours(4);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    private readonly AutoEndTestFactory _factory;
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);

    public SessionAutoEnderTests(AutoEndTestFactory factory)
    {
        _factory = factory;
    }

    /// <summary>Own database, so other classes' running sessions stay out of the sweeps.</summary>
    public sealed class AutoEndTestFactory : AnchorApiFactory { }

    [Fact]
    public async Task Ends_sessions_running_longer_than_the_max_duration_and_leaves_younger_ones()
    {
        var forgotten = await SeedSessionAsync(startedAgo: MaxDuration + TimeSpan.FromMinutes(1));
        var justOverTheLimit = await SeedSessionAsync(startedAgo: MaxDuration);
        var live = await SeedSessionAsync(startedAgo: TimeSpan.FromMinutes(50));

        var ended = await BuildAutoEnder().EndForgottenSessionsAsync(CancellationToken.None);

        Assert.Contains(forgotten.Session.Id, ended);
        Assert.Contains(justOverTheLimit.Session.Id, ended);
        Assert.DoesNotContain(live.Session.Id, ended);
        Assert.NotNull(await EndedAtAsync(forgotten.Session.Id));
        Assert.NotNull(await EndedAtAsync(justOverTheLimit.Session.Id));
        Assert.Null(await EndedAtAsync(live.Session.Id));

        var broadcaster = _factory.Services.GetRequiredService<RecordingSessionBroadcaster>();
        Assert.Contains(forgotten.Session.Id, broadcaster.SessionEndedCalls);
        Assert.DoesNotContain(live.Session.Id, broadcaster.SessionEndedCalls);
    }

    [Fact]
    public async Task A_session_that_already_ended_is_left_alone()
    {
        var seeded = await SeedSessionAsync(startedAgo: MaxDuration + TimeSpan.FromHours(1), ended: true);
        var endedAt = await EndedAtAsync(seeded.Session.Id);

        var ended = await BuildAutoEnder().EndForgottenSessionsAsync(CancellationToken.None);

        Assert.DoesNotContain(seeded.Session.Id, ended);
        Assert.Equal(endedAt, await EndedAtAsync(seeded.Session.Id));
        var broadcaster = _factory.Services.GetRequiredService<RecordingSessionBroadcaster>();
        Assert.DoesNotContain(seeded.Session.Id, broadcaster.SessionEndedCalls);
    }

    [Fact]
    public async Task Auto_ending_writes_the_same_summaries_as_the_teachers_end()
    {
        var seeded = await SeedSessionAsync(startedAgo: MaxDuration + TimeSpan.FromMinutes(10), studentCount: 2);
        var s0 = seeded.Students[0].Id;
        var s1 = seeded.Students[1].Id;
        var at = _clock.GetUtcNow() - MaxDuration;
        await SeedEventsAsync(seeded.Session.Id, new[]
        {
            (s0, EventKind.ForegroundChange, at.AddMinutes(1)),
            (s0, EventKind.ForegroundChange, at.AddMinutes(9)),
            (s0, EventKind.BlockedUrl, at.AddMinutes(3)),
            (s1, EventKind.ForegroundChange, at.AddMinutes(5)),
        });

        await BuildAutoEnder().EndForgottenSessionsAsync(CancellationToken.None);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var summaries = await db.SessionEventSummaries.AsNoTracking()
            .Where(s => s.SessionId == seeded.Session.Id)
            .ToListAsync();
        Assert.Equal(3, summaries.Count);
        var s0Foreground = summaries.Single(s => s.UserId == s0 && s.Kind == EventKind.ForegroundChange);
        Assert.Equal(2, s0Foreground.Count);
        Assert.Equal(at.AddMinutes(1), s0Foreground.FirstAt);
        Assert.Equal(at.AddMinutes(9), s0Foreground.LastAt);
        Assert.Equal(1, summaries.Single(s => s.UserId == s0 && s.Kind == EventKind.BlockedUrl).Count);
        Assert.Equal(1, summaries.Single(s => s.UserId == s1 && s.Kind == EventKind.ForegroundChange).Count);
    }

    [Fact]
    public async Task Auto_ending_drops_the_sessions_heartbeat_state_like_the_teachers_end()
    {
        var seeded = await SeedSessionAsync(startedAgo: MaxDuration + TimeSpan.FromMinutes(1), joined: true);
        var student = seeded.Students[0];
        var cache = _factory.Services.GetRequiredService<ActiveParticipantCache>();
        var tracker = _factory.Services.GetRequiredService<HeartbeatTracker>();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
            cache.Update(await db.SessionParticipants.AsNoTracking()
                .SingleAsync(p => p.SessionId == seeded.Session.Id && p.UserId == student.Id));
        }
        tracker.Record(seeded.Session.Id, student.Id, _clock.GetUtcNow());

        await BuildAutoEnder().EndForgottenSessionsAsync(CancellationToken.None);

        // #342: no cached participant may outlive the session, whoever ended it.
        Assert.False(cache.TryGet(seeded.Session.Id, student.Id, out _));
        // And no HeartbeatLost after the fact for the agent that went quiet.
        Assert.False(tracker.TryGet(seeded.Session.Id, student.Id, out _));
    }

    [Fact]
    public async Task A_joined_agent_is_told_the_session_ended()
    {
        var seeded = await SeedSessionAsync(startedAgo: MaxDuration + TimeSpan.FromMinutes(1));
        var student = seeded.Students[0];

        await using var connection = BuildConnection(student);
        var endedSignal = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<Guid>(nameof(ISessionHubClient.SessionEnded), id => endedSignal.TrySetResult(id));
        await connection.StartAsync();
        await connection.InvokeAsync<JoinSessionResult>(
            "JoinSession", new JoinSessionRequest(seeded.Session.Id, JoinCode: null));

        await BuildAutoEnder().EndForgottenSessionsAsync(CancellationToken.None);

        Assert.Equal(seeded.Session.Id, await endedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_forgotten_session_is_no_longer_offered_for_rejoining_and_moves_to_history()
    {
        // The user-facing bug: a student whose agent restarts after the lesson
        // was put back into the forgotten session through rehydration (#54).
        var seeded = await SeedSessionAsync(startedAgo: MaxDuration + TimeSpan.FromMinutes(1), joined: true);
        using var studentClient = _factory.CreateClient();
        TestAuth.SetStudent(studentClient, seeded.Students[0]);
        using var teacherClient = _factory.CreateClient();
        TestAuth.SetTeacher(teacherClient, seeded.Teacher);

        var before = await studentClient.GetFromJsonAsync<List<SessionStartedPayload>>("/sessions/rejoinable");
        Assert.Contains(before!, s => s.SessionId == seeded.Session.Id);

        await BuildAutoEnder().EndForgottenSessionsAsync(CancellationToken.None);

        var rejoinable = await studentClient.GetFromJsonAsync<List<SessionStartedPayload>>("/sessions/rejoinable");
        Assert.DoesNotContain(rejoinable!, s => s.SessionId == seeded.Session.Id);
        var active = await teacherClient.GetFromJsonAsync<List<SessionSummary>>("/sessions/active");
        Assert.DoesNotContain(active!, s => s.Id == seeded.Session.Id);
        var history = await teacherClient.GetFromJsonAsync<List<SessionHistoryEntry>>("/sessions/history");
        Assert.Contains(history!, s => s.Id == seeded.Session.Id);
    }

    [Fact]
    public async Task The_teachers_end_after_an_auto_end_returns_the_auto_end_time_without_rewriting_it()
    {
        var seeded = await SeedSessionAsync(startedAgo: MaxDuration + TimeSpan.FromMinutes(1));
        await SeedEventsAsync(seeded.Session.Id, new[]
        {
            (seeded.Students[0].Id, EventKind.ForegroundChange, _clock.GetUtcNow() - MaxDuration),
        });
        await BuildAutoEnder().EndForgottenSessionsAsync(CancellationToken.None);
        var autoEndedAt = await EndedAtAsync(seeded.Session.Id);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, seeded.Teacher);
        var response = await client.PostAsync($"/sessions/{seeded.Session.Id}/end", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<EndSessionResponse>();
        Assert.Equal(autoEndedAt, body!.EndedAt);
        Assert.Equal(autoEndedAt, await EndedAtAsync(seeded.Session.Id));
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        Assert.Equal(1, await db.SessionEventSummaries.CountAsync(s => s.SessionId == seeded.Session.Id));
    }

    [Fact]
    public async Task A_zero_max_duration_turns_auto_ending_off()
    {
        var seeded = await SeedSessionAsync(startedAgo: TimeSpan.FromDays(3));

        var ended = await BuildAutoEnder(maxDuration: TimeSpan.Zero).EndForgottenSessionsAsync(CancellationToken.None);

        Assert.Empty(ended);
        Assert.Null(await EndedAtAsync(seeded.Session.Id));
    }

    [Fact]
    public async Task Background_loop_waits_a_sweep_interval_before_its_first_sweep()
    {
        // #344: a (re)start must not query the database, so the first sweep comes
        // one interval after start, not at start.
        var clock = new TimerRecordingTimeProvider(_clock.GetUtcNow());
        var seeded = await SeedSessionAsync(startedAgo: MaxDuration + TimeSpan.FromMinutes(1));
        var timeout = TimeSpan.FromSeconds(10);

        var autoEnder = BuildAutoEnder(clock: clock);
        await autoEnder.StartAsync(CancellationToken.None);
        try
        {
            var firstWait = await clock.WaitForTimerAsync(_ => true, timeout);
            Assert.Equal(SweepInterval, firstWait);
            Assert.Null(await EndedAtAsync(seeded.Session.Id));

            clock.Advance(SweepInterval);
            Assert.True(
                await PollAsync(async () => await EndedAtAsync(seeded.Session.Id) is not null, timeout),
                "The first scheduled sweep did not end the forgotten session.");
        }
        finally
        {
            await autoEnder.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<bool> PollAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(50);
        }
        return await condition();
    }

    private SessionAutoEnder BuildAutoEnder(TimeSpan? maxDuration = null, TimeProvider? clock = null)
    {
        var options = new TestOptionsMonitor(new SessionAutoEndOptions
        {
            MaxDuration = maxDuration ?? MaxDuration,
            SweepInterval = SweepInterval,
        });
        return new SessionAutoEnder(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            clock ?? _clock,
            options,
            NullLogger<SessionAutoEnder>.Instance);
    }

    private async Task<SeededSession> SeedSessionAsync(
        TimeSpan startedAgo, int studentCount = 1, bool joined = false, bool ended = false)
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount);
        var startedAt = _clock.GetUtcNow() - startedAgo;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var session = new Session
        {
            TeacherId = scenario.Teacher.Id,
            ClassId = scenario.Class.Id,
            StartedAt = startedAt,
            EndedAt = ended ? startedAt.AddMinutes(50) : null,
            JoinCode = Random.Shared.Next(0, 1_000_000).ToString("D6"),
        };
        db.Sessions.Add(session);
        foreach (var student in scenario.Students)
        {
            db.SessionParticipants.Add(new SessionParticipant
            {
                SessionId = session.Id,
                UserId = student.Id,
                JoinedAt = joined ? startedAt.AddMinutes(1) : null,
            });
        }
        await db.SaveChangesAsync();
        return new SeededSession(session, scenario.Teacher, scenario.Students);
    }

    private async Task SeedEventsAsync(
        Guid sessionId,
        IEnumerable<(Guid UserId, EventKind Kind, DateTimeOffset OccurredAt)> events)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        foreach (var (userId, kind, occurredAt) in events)
        {
            db.Events.Add(new Event
            {
                SessionId = sessionId,
                UserId = userId,
                Kind = kind,
                PayloadJson = "{}",
                OccurredAt = occurredAt,
            });
        }
        await db.SaveChangesAsync();
    }

    private async Task<DateTimeOffset?> EndedAtAsync(Guid sessionId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        return await db.Sessions.AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Select(s => s.EndedAt)
            .SingleAsync();
    }

    private HubConnection BuildConnection(User student)
    {
        var server = _factory.Server;
        return new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, SessionHub.Path.TrimStart('/')), options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers[FakeJwtBearerHandler.HeaderOid] = student.EntraOid.ToString();
                options.Headers[FakeJwtBearerHandler.HeaderRole] = "Student";
                options.Headers[FakeJwtBearerHandler.HeaderName] = student.DisplayName;
            })
            .Build();
    }

    private sealed record SeededSession(Session Session, User Teacher, IReadOnlyList<User> Students);

    private sealed class TestOptionsMonitor : IOptionsMonitor<SessionAutoEndOptions>
    {
        public TestOptionsMonitor(SessionAutoEndOptions value) { CurrentValue = value; }
        public SessionAutoEndOptions CurrentValue { get; }
        public SessionAutoEndOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<SessionAutoEndOptions, string?> listener) => null;
    }
}
