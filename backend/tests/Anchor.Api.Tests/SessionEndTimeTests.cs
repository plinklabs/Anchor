using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anchor.Api.Controllers;
using Anchor.Api.Realtime;
using Anchor.Api.Sessions;
using Anchor.Api.Tests.FakeAuth;
using Anchor.Domain.Schools;
using Anchor.Domain.Sessions;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Anchor.Api.Tests;

/// <summary>
/// Starting a session asks for its end (#447): the end of this lesson period,
/// of the next one, or a specific time. The period ends come from the lesson
/// periods an admin entered for the class's school, one timetable for every
/// school day in school time (Europe/Brussels), with the last five minutes of a
/// period rolling over to the next. And once a session is past its planned
/// end, no agent or extension is put back into it, even in the moment before
/// the auto-end marks it ended.
///
/// <para>
/// Driven through the REST API and real hub connections, on a fake clock the
/// whole backend reads. Expected instants are written out in UTC, so that a
/// daylight-saving mistake shows: 09:15 in Brussels is 07:15Z in summer and
/// 08:15Z in winter.
/// </para>
/// </summary>
public sealed class SessionEndTimeTests : IClassFixture<SessionEndTimeTests.EndTimeTestFactory>
{
    private static readonly TimeSpan MaxDuration = TimeSpan.FromHours(4);

    /// <summary>
    /// The timetable the tests' schools use: two periods back to back, a break,
    /// two more, lunch, and a last one.
    /// </summary>
    private static readonly (string Start, string End)[] Timetable =
    {
        ("08:25", "09:15"),
        ("09:15", "10:05"),
        ("10:20", "11:10"),
        ("11:10", "12:00"),
        ("13:00", "13:50"),
    };

    private readonly EndTimeTestFactory _factory;

    public SessionEndTimeTests(EndTimeTestFactory factory)
    {
        _factory = factory;
    }

    /// <summary>Own database and a fake clock the whole backend reads.</summary>
    public sealed class EndTimeTestFactory : AnchorApiFactory
    {
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

    // ---------- What the start form offers: GET /sessions/end-options ----------

    [Fact]
    public async Task The_end_options_are_the_end_of_this_period_and_of_the_next()
    {
        var scenario = await SeedClassAsync(Timetable);
        // Monday 16 November 2026, 08:40 in Brussels (CET, UTC+1): period 1.
        SetClock("2026-11-16T07:40:00Z");

        var options = await EndOptionsAsync(scenario);

        Assert.Equal(Utc("2026-11-16T08:15:00Z"), options.ThisPeriodEndsAt); // 09:15
        Assert.Equal(Utc("2026-11-16T09:05:00Z"), options.NextPeriodEndsAt); // 10:05
        Assert.Equal(Utc("2026-11-16T07:40:00Z") + MaxDuration, options.LatestEndsAt);
    }

    [Theory]
    // 09:09 in Brussels: six minutes left in period 1, which is the period.
    [InlineData("2026-11-16T08:09:00Z", "2026-11-16T08:15:00Z", "2026-11-16T09:05:00Z")]
    // 09:10, five minutes left, and 09:14: the lesson meant is period 2, and
    // the next one is period 3, after the break (11:10).
    [InlineData("2026-11-16T08:10:00Z", "2026-11-16T09:05:00Z", "2026-11-16T10:10:00Z")]
    [InlineData("2026-11-16T08:14:00Z", "2026-11-16T09:05:00Z", "2026-11-16T10:10:00Z")]
    public async Task A_start_in_the_last_five_minutes_of_a_period_belongs_to_the_next_one(
        string now, string thisPeriodEnd, string nextPeriodEnd)
    {
        var scenario = await SeedClassAsync(Timetable);
        SetClock(now);

        var options = await EndOptionsAsync(scenario);

        Assert.Equal(Utc(thisPeriodEnd), options.ThisPeriodEndsAt);
        Assert.Equal(Utc(nextPeriodEnd), options.NextPeriodEndsAt);
    }

    [Theory]
    // Summer (CEST, UTC+2): Monday 15 June 2026, 08:40 → period 1 ends 09:15.
    [InlineData("2026-06-15T06:40:00Z", "2026-06-15T07:15:00Z", "2026-06-15T08:05:00Z")]
    // The last school day before the clocks go back (25 October 2026), CEST.
    [InlineData("2026-10-23T06:40:00Z", "2026-10-23T07:15:00Z", "2026-10-23T08:05:00Z")]
    // The first school day after, CET (UTC+1): the same 08:40 and 09:15.
    [InlineData("2026-10-26T07:40:00Z", "2026-10-26T08:15:00Z", "2026-10-26T09:05:00Z")]
    // A start at 07:40Z is 08:40 in winter, but 09:40 in summer: period 2.
    [InlineData("2026-06-15T07:40:00Z", "2026-06-15T08:05:00Z", "2026-06-15T09:10:00Z")]
    public async Task Daylight_saving_time_keeps_the_periods_on_the_school_clock(
        string now, string thisPeriodEnd, string nextPeriodEnd)
    {
        var scenario = await SeedClassAsync(Timetable);
        SetClock(now);

        var options = await EndOptionsAsync(scenario);

        Assert.Equal(Utc(thisPeriodEnd), options.ThisPeriodEndsAt);
        Assert.Equal(Utc(nextPeriodEnd), options.NextPeriodEndsAt);
    }

    [Theory]
    // The break between periods 2 and 3 (10:10 in Brussels).
    [InlineData("2026-11-16T09:10:00Z")]
    // Before school (07:00) and after it (14:00).
    [InlineData("2026-11-16T06:00:00Z")]
    [InlineData("2026-11-16T13:00:00Z")]
    // 13:47, in the last minutes of the day's last period: no lesson follows.
    [InlineData("2026-11-16T12:47:00Z")]
    public async Task Outside_every_period_only_a_specific_time_is_offered(string now)
    {
        var scenario = await SeedClassAsync(Timetable);
        SetClock(now);

        var options = await EndOptionsAsync(scenario);

        Assert.Null(options.ThisPeriodEndsAt);
        Assert.Null(options.NextPeriodEndsAt);
        Assert.Equal(Utc(now) + MaxDuration, options.LatestEndsAt);
    }

    [Fact]
    public async Task In_the_last_period_of_the_day_there_is_no_next_period()
    {
        var scenario = await SeedClassAsync(Timetable);
        SetClock("2026-11-16T12:30:00Z"); // 13:30

        var options = await EndOptionsAsync(scenario);

        Assert.Equal(Utc("2026-11-16T12:50:00Z"), options.ThisPeriodEndsAt);
        Assert.Null(options.NextPeriodEndsAt);
    }

    [Fact]
    public async Task A_school_without_lesson_periods_only_offers_a_specific_time()
    {
        SetClock("2026-11-16T07:40:00Z");
        var withoutPeriods = await SeedClassAsync(Array.Empty<(string, string)>());
        var withoutSchool = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 1);

        foreach (var scenario in new[] { withoutPeriods, withoutSchool })
        {
            var options = await EndOptionsAsync(scenario);
            Assert.Null(options.ThisPeriodEndsAt);
            Assert.Null(options.NextPeriodEndsAt);
            Assert.NotNull(options.LatestEndsAt);
        }
    }

    [Fact]
    public async Task Only_a_teacher_of_the_class_gets_its_end_options()
    {
        var scenario = await SeedClassAsync(Timetable);
        var otherTeacher = await TestSeed.AddUserAsync(_factory, UserRole.Teacher, "Other " + Suffix());

        using var other = _factory.CreateClient();
        TestAuth.SetTeacher(other, otherTeacher);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await other.GetAsync($"/sessions/end-options?classId={scenario.Class.Id}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await other.GetAsync($"/sessions/end-options?classId={Guid.NewGuid()}")).StatusCode);

        using var student = _factory.CreateClient();
        TestAuth.SetStudent(student, scenario.Students[0]);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await student.GetAsync($"/sessions/end-options?classId={scenario.Class.Id}")).StatusCode);
    }

    // ---------- Starting with an end: POST /sessions ----------

    [Fact]
    public async Task Ending_at_the_end_of_this_period_plans_the_session_to_end_then()
    {
        var scenario = await SeedClassAsync(Timetable);
        SetClock("2026-11-16T07:40:00Z"); // 08:40, period 1

        var started = await StartAsync(scenario, new StartSessionRequest(
            scenario.Class.Id, null, SessionEndMode.ThisPeriod));

        Assert.Equal(Utc("2026-11-16T08:15:00Z"), started.EndsAt);
        using var teacher = TeacherClient(scenario);
        var detail = await teacher.GetFromJsonAsync<SessionDetailResponse>($"/sessions/{started.Id}");
        Assert.Equal(Utc("2026-11-16T08:15:00Z"), detail!.EndsAt);
        var active = await teacher.GetFromJsonAsync<List<SessionSummary>>("/sessions/active");
        Assert.Equal(Utc("2026-11-16T08:15:00Z"), active!.Single(s => s.Id == started.Id).EndsAt);
    }

    [Fact]
    public async Task Ending_at_the_end_of_the_next_period_covers_a_double_period()
    {
        var scenario = await SeedClassAsync(Timetable);
        SetClock("2026-11-16T07:40:00Z"); // 08:40, period 1

        var started = await StartAsync(scenario, new StartSessionRequest(
            scenario.Class.Id, null, SessionEndMode.NextPeriod));

        Assert.Equal(Utc("2026-11-16T09:05:00Z"), started.EndsAt); // 10:05
    }

    [Fact]
    public async Task The_period_is_worked_out_when_the_session_starts_with_the_rollover()
    {
        var scenario = await SeedClassAsync(Timetable);
        // The form showed period 1's end, but the teacher clicked Start at
        // 09:12: the session is for period 2.
        SetClock("2026-11-16T08:12:00Z");

        var started = await StartAsync(scenario, new StartSessionRequest(
            scenario.Class.Id, null, SessionEndMode.ThisPeriod));

        Assert.Equal(Utc("2026-11-16T09:05:00Z"), started.EndsAt); // 10:05
    }

    [Fact]
    public async Task A_period_end_asked_for_outside_every_period_is_refused_and_starts_nothing()
    {
        var scenario = await SeedClassAsync(Timetable);
        SetClock("2026-11-16T09:10:00Z"); // 10:10, the break

        using var teacher = TeacherClient(scenario);
        var response = await teacher.PostAsJsonAsync("/sessions", new StartSessionRequest(
            scenario.Class.Id, null, SessionEndMode.ThisPeriod));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("noLessonPeriod").GetBoolean());
        var active = await teacher.GetFromJsonAsync<List<SessionSummary>>("/sessions/active");
        Assert.DoesNotContain(active!, s => s.ClassId == scenario.Class.Id);
    }

    [Fact]
    public async Task A_specific_end_time_has_to_lie_between_now_and_the_max_duration()
    {
        var scenario = await SeedClassAsync(Array.Empty<(string, string)>());
        SetClock("2026-11-16T09:10:00Z");
        var now = Utc("2026-11-16T09:10:00Z");
        using var teacher = TeacherClient(scenario);

        foreach (var refused in new[]
                 {
                     new StartSessionRequest(scenario.Class.Id, null, SessionEndMode.SpecificTime, now),
                     new StartSessionRequest(scenario.Class.Id, null, SessionEndMode.SpecificTime, now.AddMinutes(-5)),
                     new StartSessionRequest(scenario.Class.Id, null, SessionEndMode.SpecificTime, now + MaxDuration + TimeSpan.FromMinutes(1)),
                     new StartSessionRequest(scenario.Class.Id, null, SessionEndMode.SpecificTime),
                 })
        {
            var response = await teacher.PostAsJsonAsync("/sessions", refused);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var at = await StartAsync(scenario, new StartSessionRequest(
            scenario.Class.Id, null, SessionEndMode.SpecificTime, now.AddMinutes(50)));
        Assert.Equal(now.AddMinutes(50), at.EndsAt);

        // An end time without a mode is a specific time too.
        var bare = await StartAsync(scenario, new StartSessionRequest(
            scenario.Class.Id, null, EndsAt: now + MaxDuration));
        Assert.Equal(now + MaxDuration, bare.EndsAt);
    }

    [Fact]
    public async Task A_session_started_without_an_end_has_no_planned_end()
    {
        var scenario = await SeedClassAsync(Timetable);
        SetClock("2026-11-16T07:40:00Z");

        var started = await StartAsync(scenario, new StartSessionRequest(scenario.Class.Id, null));

        Assert.Null(started.EndsAt);
    }

    // ---------- Past its planned end, nothing pulls a device back in ----------
    //
    // These sessions are past their planned end but not marked ended: the
    // moment between the end and the auto-end getting to it, which after a
    // backend restart lasts until its first sweep. No auto-end runs here.

    [Fact]
    public async Task A_session_past_its_planned_end_is_not_offered_for_rejoining()
    {
        var (scenario, sessionId) = await SeedJoinedSessionAsync(endsIn: TimeSpan.FromMinutes(1));
        using var student = _factory.CreateClient();
        TestAuth.SetStudent(student, scenario.Students[0]);

        var before = await student.GetFromJsonAsync<List<SessionStartedPayload>>("/sessions/rejoinable");
        Assert.Contains(before!, s => s.SessionId == sessionId);

        _factory.Clock.Advance(TimeSpan.FromMinutes(1));

        var after = await student.GetFromJsonAsync<List<SessionStartedPayload>>("/sessions/rejoinable");
        Assert.DoesNotContain(after!, s => s.SessionId == sessionId);
        Assert.Null(await EndedAtAsync(sessionId));
    }

    [Fact]
    public async Task The_catch_up_and_the_reconnect_check_leave_out_a_session_past_its_planned_end()
    {
        var (scenario, sessionId) = await SeedJoinedSessionAsync(endsIn: TimeSpan.FromMinutes(1));
        await using var agent = Connect(scenario.Students[0], "Student");
        await agent.StartAndAwaitOnConnectedAsync();

        // Control: while it runs, the agent catches up with it and stays in it.
        var caughtUp = await agent.InvokeAsync<SessionStartedPayload?>(nameof(SessionHub.GetStartedSession));
        Assert.Equal(sessionId, caughtUp?.SessionId);
        Assert.True(await agent.InvokeAsync<bool>(nameof(SessionHub.IsInSession), sessionId));

        _factory.Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Null(await agent.InvokeAsync<SessionStartedPayload?>(nameof(SessionHub.GetStartedSession)));
        Assert.False(await agent.InvokeAsync<bool>(nameof(SessionHub.IsInSession), sessionId));
    }

    [Fact]
    public async Task Nothing_joins_a_session_past_its_planned_end_but_its_teacher_still_hears_it_end()
    {
        var (scenario, sessionId) = await SeedJoinedSessionAsync(endsIn: TimeSpan.FromMinutes(1), joined: false);
        var joinCode = await JoinCodeAsync(sessionId);
        var outsider = await TestSeed.AddUserAsync(_factory, UserRole.Student, "Outsider " + Suffix());
        _factory.Clock.Advance(TimeSpan.FromMinutes(1));

        await using var agent = Connect(scenario.Students[0], "Student");
        await agent.StartAndAwaitOnConnectedAsync();
        await Assert.ThrowsAsync<HubException>(() => agent.InvokeAsync<JoinSessionResult>(
            nameof(SessionHub.JoinSession), new JoinSessionRequest(sessionId, JoinCode: null)));

        using var byCode = _factory.CreateClient();
        TestAuth.SetStudent(byCode, outsider);
        var response = await byCode.PostAsJsonAsync("/sessions/join-by-code", new JoinByCodeRequest(joinCode));
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);

        // The teacher's live page subscribes to the roster feed, to see it end.
        await using var livePage = Connect(scenario.Teacher, "Teacher");
        await livePage.StartAndAwaitOnConnectedAsync();
        var joined = await livePage.InvokeAsync<JoinSessionResult>(
            nameof(SessionHub.JoinSession), new JoinSessionRequest(sessionId, JoinCode: null));
        Assert.Equal(sessionId, joined.SessionId);
    }

    // ---------- Helpers ----------

    private void SetClock(string utc) => _factory.Clock.AdjustTime(Utc(utc));

    private static DateTimeOffset Utc(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);

    private static string Suffix() => Guid.NewGuid().ToString("N")[..6];

    private async Task<TestScenario> SeedClassAsync((string Start, string End)[] periods)
    {
        var school = "School " + Suffix();
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(
            _factory, studentCount: 1, schoolTag: school, classCode: "3A");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var row = new School { Name = school, IsActive = true };
        db.Schools.Add(row);
        foreach (var (start, end) in periods)
        {
            db.LessonPeriods.Add(new LessonPeriod
            {
                SchoolId = row.Id,
                Start = TimeOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture),
                End = TimeOnly.Parse(end, System.Globalization.CultureInfo.InvariantCulture),
            });
        }
        await db.SaveChangesAsync();
        return scenario;
    }

    private HttpClient TeacherClient(TestScenario scenario)
    {
        var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);
        return client;
    }

    private async Task<SessionEndOptionsResponse> EndOptionsAsync(TestScenario scenario)
    {
        using var teacher = TeacherClient(scenario);
        var options = await teacher.GetFromJsonAsync<SessionEndOptionsResponse>(
            $"/sessions/end-options?classId={scenario.Class.Id}");
        return options!;
    }

    private async Task<StartSessionResponse> StartAsync(TestScenario scenario, StartSessionRequest request)
    {
        using var teacher = TeacherClient(scenario);
        var response = await teacher.PostAsJsonAsync("/sessions", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<StartSessionResponse>())!;
    }

    /// <summary>
    /// A running session planned to end <paramref name="endsIn"/> from now,
    /// whose one student's agent joined it (or, with <paramref name="joined"/>
    /// false, hasn't yet).
    /// </summary>
    private async Task<(TestScenario Scenario, Guid SessionId)> SeedJoinedSessionAsync(
        TimeSpan endsIn, bool joined = true)
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 1);
        var now = _factory.Clock.GetUtcNow();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        var session = new Session
        {
            TeacherId = scenario.Teacher.Id,
            ClassId = scenario.Class.Id,
            StartedAt = now - TimeSpan.FromMinutes(40),
            EndsAt = now + endsIn,
            JoinCode = Random.Shared.Next(0, 1_000_000).ToString("D6"),
        };
        db.Sessions.Add(session);
        db.SessionParticipants.Add(new SessionParticipant
        {
            SessionId = session.Id,
            UserId = scenario.Students[0].Id,
            JoinedAt = joined ? session.StartedAt.AddMinutes(1) : null,
        });
        await db.SaveChangesAsync();
        return (scenario, session.Id);
    }

    private async Task<string> JoinCodeAsync(Guid sessionId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        return await db.Sessions.AsNoTracking().Where(s => s.Id == sessionId).Select(s => s.JoinCode).SingleAsync();
    }

    private async Task<DateTimeOffset?> EndedAtAsync(Guid sessionId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        return await db.Sessions.AsNoTracking().Where(s => s.Id == sessionId).Select(s => s.EndedAt).SingleAsync();
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
}
