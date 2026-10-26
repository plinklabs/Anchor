using System.Net;
using System.Net.Http.Json;
using Anchor.Api.Controllers;
using Anchor.Api.Realtime;
using Anchor.Api.Sessions;
using Anchor.Api.Tests.FakeAuth;
using Anchor.Domain.Sessions;
using Anchor.Domain.Users;
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

namespace Anchor.Api.Tests;

/// <summary>
/// A session ends at its planned end (#447), through the same path as the
/// teacher's End: the joined agents and extensions get <c>SessionEnded</c>, and
/// the session moves to history and out of rehydration. The auto-end wakes for
/// the planned end itself, not for its next sweep minutes later, and a backend
/// restart, which empties its schedule, leaves no gap either.
///
/// <para>
/// Each test runs its own <see cref="SessionAutoEnder"/> loop on the fake clock
/// the whole backend reads, with the schedule the backend fills. The timers the
/// loop creates are told apart by their due time, so each test waits on a due
/// time of its own.
/// </para>
/// </summary>
public sealed class SessionPlannedEndTests : IClassFixture<SessionPlannedEndTests.PlannedEndTestFactory>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly PlannedEndTestFactory _factory;

    public SessionPlannedEndTests(PlannedEndTestFactory factory)
    {
        _factory = factory;
    }

    /// <summary>Own database and schedule, and a fake clock the whole backend reads.</summary>
    public sealed class PlannedEndTestFactory : AnchorApiFactory
    {
        public TimerRecordingTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

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
    public async Task A_session_ends_at_its_planned_end_without_waiting_for_a_sweep()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 1);
        var student = scenario.Students[0];
        var plannedEnd = _factory.Clock.GetUtcNow() + TimeSpan.FromMinutes(3);
        var sessionId = await StartAsync(scenario, plannedEnd);

        await using var agent = Connect(student, "Student");
        var ended = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.On<Guid>(nameof(ISessionHubClient.SessionEnded), id => ended.TrySetResult(id));
        await agent.StartAndAwaitOnConnectedAsync();
        await agent.InvokeAsync<JoinSessionResult>(
            nameof(SessionHub.JoinSession), new JoinSessionRequest(sessionId, JoinCode: null));

        var autoEnder = BuildAutoEnder(sweepInterval: TimeSpan.FromMinutes(5));
        await autoEnder.StartAsync(CancellationToken.None);
        try
        {
            // It waits for the planned end, three minutes out, not for its
            // first sweep in five.
            await _factory.Clock.WaitForTimerAsync(due => due == TimeSpan.FromMinutes(3), Timeout);
            Assert.Null(await EndedAtAsync(sessionId));

            _factory.Clock.Advance(TimeSpan.FromMinutes(3));

            Assert.Equal(sessionId, await ended.Task.WaitAsync(Timeout));
            Assert.Equal(plannedEnd, await EndedAtAsync(sessionId));
        }
        finally
        {
            await autoEnder.StopAsync(CancellationToken.None);
        }

        // Ended as the teacher's End ends it: history, not active, not rejoinable.
        using var teacher = Client(scenario.Teacher, "Teacher");
        var history = await teacher.GetFromJsonAsync<List<SessionHistoryEntry>>("/sessions/history");
        Assert.Equal(plannedEnd, history!.Single(s => s.Id == sessionId).EndsAt);
        var active = await teacher.GetFromJsonAsync<List<SessionSummary>>("/sessions/active");
        Assert.DoesNotContain(active!, s => s.Id == sessionId);
        using var studentClient = Client(student, "Student");
        var rejoinable = await studentClient.GetFromJsonAsync<List<SessionStartedPayload>>("/sessions/rejoinable");
        Assert.DoesNotContain(rejoinable!, s => s.SessionId == sessionId);
        var broadcaster = _factory.Services.GetRequiredService<RecordingSessionBroadcaster>();
        var call = Assert.Single(broadcaster.SessionEndedCalls, c => c.SessionId == sessionId);
        Assert.Contains(scenario.Teacher.Id, call.RecipientUserIds);
        Assert.Contains(student.Id, call.RecipientUserIds);
    }

    [Fact]
    public async Task A_session_started_while_the_auto_end_waits_wakes_it_for_its_earlier_end()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 1);

        var autoEnder = BuildAutoEnder(sweepInterval: TimeSpan.FromMinutes(7));
        await autoEnder.StartAsync(CancellationToken.None);
        try
        {
            // Parked on its sweep, seven minutes out.
            await _factory.Clock.WaitForTimerAsync(due => due == TimeSpan.FromMinutes(7), Timeout);

            var sessionId = await StartAsync(scenario, _factory.Clock.GetUtcNow() + TimeSpan.FromMinutes(2));

            await _factory.Clock.WaitForTimerAsync(due => due == TimeSpan.FromMinutes(2), Timeout);
            _factory.Clock.Advance(TimeSpan.FromMinutes(2));

            Assert.True(
                await PollAsync(async () => await EndedAtAsync(sessionId) is not null),
                "The session did not end at its planned end, two minutes before the sweep.");
        }
        finally
        {
            await autoEnder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task After_a_restart_a_session_past_its_planned_end_ends_as_soon_as_an_agent_asks_about_it()
    {
        // The backend restarted after this session started: its schedule is
        // empty, and the first sweep is a whole interval away (#344). The
        // student's agent reconnects and asks whether it is still in it.
        var (scenario, sessionId) = await SeedJoinedSessionAsync(endsIn: TimeSpan.FromMinutes(-1));
        await using var agent = Connect(scenario.Students[0], "Student");
        await agent.StartAndAwaitOnConnectedAsync();

        var autoEnder = BuildAutoEnder(sweepInterval: TimeSpan.FromMinutes(11));
        await autoEnder.StartAsync(CancellationToken.None);
        try
        {
            await _factory.Clock.WaitForTimerAsync(due => due == TimeSpan.FromMinutes(11), Timeout);

            Assert.False(await agent.InvokeAsync<bool>(nameof(SessionHub.IsInSession), sessionId));

            // Ended without the clock moving: not at the sweep, eleven minutes on.
            Assert.True(
                await PollAsync(async () => await EndedAtAsync(sessionId) is not null),
                "The session past its planned end was left running until the next sweep.");
        }
        finally
        {
            await autoEnder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_sweep_ends_sessions_past_a_planned_end_it_missed_and_schedules_the_rest()
    {
        var (_, missed) = await SeedJoinedSessionAsync(endsIn: TimeSpan.FromMinutes(-2));
        var (_, upcoming) = await SeedJoinedSessionAsync(endsIn: TimeSpan.FromMinutes(20));
        var schedule = new SessionEndSchedule();

        var ended = await BuildAutoEnder(schedule: schedule).EndForgottenSessionsAsync(CancellationToken.None);

        Assert.Contains(missed, ended);
        Assert.DoesNotContain(upcoming, ended);
        Assert.NotNull(await EndedAtAsync(missed));
        Assert.Null(await EndedAtAsync(upcoming));
        Assert.Contains(upcoming, schedule.TakeDue(_factory.Clock.GetUtcNow() + TimeSpan.FromMinutes(20)));
    }

    [Fact]
    public async Task Ending_a_session_early_takes_its_planned_end_off_the_schedule()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 1);
        var sessionId = await StartAsync(scenario, _factory.Clock.GetUtcNow() + TimeSpan.FromMinutes(30));

        using var teacher = Client(scenario.Teacher, "Teacher");
        var response = await teacher.PostAsync($"/sessions/{sessionId}/end", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var schedule = _factory.Services.GetRequiredService<SessionEndSchedule>();
        Assert.DoesNotContain(sessionId, schedule.TakeDue(_factory.Clock.GetUtcNow() + TimeSpan.FromDays(1)));
    }

    // ---------- Helpers ----------

    private SessionAutoEnder BuildAutoEnder(TimeSpan? sweepInterval = null, SessionEndSchedule? schedule = null) =>
        new(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            _factory.Clock,
            new StaticOptionsMonitor(new SessionAutoEndOptions
            {
                MaxDuration = TimeSpan.FromHours(4),
                SweepInterval = sweepInterval ?? TimeSpan.FromMinutes(5),
            }),
            schedule ?? _factory.Services.GetRequiredService<SessionEndSchedule>(),
            NullLogger<SessionAutoEnder>.Instance);

    private async Task<Guid> StartAsync(TestScenario scenario, DateTimeOffset endsAt)
    {
        using var teacher = Client(scenario.Teacher, "Teacher");
        var response = await teacher.PostAsJsonAsync("/sessions", new StartSessionRequest(
            scenario.Class.Id, null, SessionEndMode.SpecificTime, endsAt));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var started = await response.Content.ReadFromJsonAsync<StartSessionResponse>();
        Assert.Equal(endsAt, started!.EndsAt);
        return started.Id;
    }

    private async Task<(TestScenario Scenario, Guid SessionId)> SeedJoinedSessionAsync(TimeSpan endsIn)
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 1);
        var now = _factory.Clock.GetUtcNow();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var session = new Session
        {
            TeacherId = scenario.Teacher.Id,
            ClassId = scenario.Class.Id,
            StartedAt = now - TimeSpan.FromMinutes(45),
            EndsAt = now + endsIn,
            JoinCode = Random.Shared.Next(0, 1_000_000).ToString("D6"),
        };
        db.Sessions.Add(session);
        db.SessionParticipants.Add(new SessionParticipant
        {
            SessionId = session.Id,
            UserId = scenario.Students[0].Id,
            JoinedAt = session.StartedAt.AddMinutes(1),
        });
        await db.SaveChangesAsync();
        return (scenario, session.Id);
    }

    private async Task<DateTimeOffset?> EndedAtAsync(Guid sessionId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        return await db.Sessions.AsNoTracking().Where(s => s.Id == sessionId).Select(s => s.EndedAt).SingleAsync();
    }

    private static async Task<bool> PollAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(50);
        }
        return await condition();
    }

    private HttpClient Client(User user, string role)
    {
        var client = _factory.CreateClient();
        TestAuth.Set(client, user, role);
        return client;
    }

    private HubConnection Connect(User user, string role)
    {
        var server = _factory.Server;
        return new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, SessionHub.Path.TrimStart('/')), options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers[FakeJwtBearerHandler.HeaderOid] = user.EntraOid.ToString();
                options.Headers[FakeJwtBearerHandler.HeaderRole] = role;
                options.Headers[FakeJwtBearerHandler.HeaderName] = user.DisplayName;
            })
            .Build();
    }

    private sealed class StaticOptionsMonitor(SessionAutoEndOptions value) : IOptionsMonitor<SessionAutoEndOptions>
    {
        public SessionAutoEndOptions CurrentValue { get; } = value;
        public SessionAutoEndOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<SessionAutoEndOptions, string?> listener) => null;
    }
}
