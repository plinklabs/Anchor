using System.Net;
using System.Net.Http.Json;
using Anchor.Api.Controllers;
using Anchor.Domain.Events;
using Anchor.Domain.Sessions;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Anchor.Api.Tests;

/// <summary>
/// <c>GET /sessions/{id}/events</c> (#492): the session's stored events, newest
/// first, from which the live page's activity feed loads what happened before
/// the teacher opened it again. Only the owning teacher reads them.
/// </summary>
public sealed class SessionEventsEndpointTests : IClassFixture<AnchorApiFactory>
{
    private readonly AnchorApiFactory _factory;

    public SessionEventsEndpointTests(AnchorApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Unauthenticated_returns_401()
    {
        var (_, session) = await SeedSessionAsync();

        using var client = _factory.CreateClient();
        var response = await client.GetAsync($"/sessions/{session.Id}/events");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Returns_404_for_a_missing_session()
    {
        var (scenario, _) = await SeedSessionAsync();

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);
        var response = await client.GetAsync($"/sessions/{Guid.NewGuid()}/events");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Returns_403_to_a_participating_student_and_none_of_the_events()
    {
        // The events name every student and the sites they asked to open: the
        // owning teacher's view, like the detail (#369).
        var (scenario, session) = await SeedSessionAsync();
        await AddEventAsync(session.Id, scenario.Students[0].Id, EventKind.UnblockRequest,
            """{"url":"https://www.reddit.com/r/aww","host":"www.reddit.com"}""", minutesAgo: 1);

        using var client = _factory.CreateClient();
        TestAuth.SetStudent(client, scenario.Students[1]);
        var response = await client.GetAsync($"/sessions/{session.Id}/events");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("reddit", body);
    }

    [Theory]
    [InlineData("Teacher")]
    [InlineData("Admin")]
    public async Task Returns_403_to_anyone_but_the_owning_teacher(string role)
    {
        var (_, session) = await SeedSessionAsync();
        var outsider = await TestSeed.AddUserAsync(_factory, Enum.Parse<UserRole>(role), "Outsider");

        using var client = _factory.CreateClient();
        TestAuth.Set(client, outsider, role);
        var response = await client.GetAsync($"/sessions/{session.Id}/events");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Gives_the_owning_teacher_every_event_newest_first()
    {
        var (scenario, session) = await SeedSessionAsync();
        var ada = scenario.Students[0];
        var bo = scenario.Students[1];
        await AddEventAsync(session.Id, ada.Id, EventKind.ForegroundChange, """{"processName":"notepad"}""", minutesAgo: 5);
        await AddEventAsync(session.Id, ada.Id, EventKind.UnblockRequest,
            """{"url":"https://chat.example.com/","host":"chat.example.com"}""", minutesAgo: 4);
        await AddEventAsync(session.Id, bo.Id, EventKind.ManualLeave, "{}", minutesAgo: 2);
        await AddEventAsync(session.Id, ada.Id, EventKind.TamperDetected, """{"kind":"inprivate_opened"}""", minutesAgo: 3);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);
        var events = await client.GetFromJsonAsync<List<SessionRecentEvent>>($"/sessions/{session.Id}/events");

        Assert.NotNull(events);
        Assert.Equal(
            [EventKind.ManualLeave, EventKind.TamperDetected, EventKind.UnblockRequest, EventKind.ForegroundChange],
            events!.Select(e => e.Kind));
        Assert.Equal([bo.Id, ada.Id, ada.Id, ada.Id], events.Select(e => e.UserId));
        Assert.Contains("chat.example.com", events[2].PayloadJson);
    }

    [Fact]
    public async Task Gives_only_the_kinds_asked_for()
    {
        // The feed reads a few kinds; the agent's foreground changes would fill
        // any page of events otherwise.
        var (scenario, session) = await SeedSessionAsync();
        var ada = scenario.Students[0];
        for (var i = 0; i < 5; i++)
            await AddEventAsync(session.Id, ada.Id, EventKind.ForegroundChange, """{"processName":"notepad"}""", minutesAgo: i);
        await AddEventAsync(session.Id, ada.Id, EventKind.HeartbeatLost, """{"lastSeenAt":"2026-11-11T10:00:00+00:00"}""", minutesAgo: 10);
        await AddEventAsync(session.Id, ada.Id, EventKind.AgentReconnected, """{"reconnectedAt":"2026-11-11T10:01:00+00:00"}""", minutesAgo: 9);
        await AddEventAsync(session.Id, ada.Id, EventKind.BlockedUrl, """{"host":"tiktok.com"}""", minutesAgo: 8);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);
        var events = await client.GetFromJsonAsync<List<SessionRecentEvent>>(
            $"/sessions/{session.Id}/events?kind=HeartbeatLost&kind=AgentReconnected&limit=3");

        Assert.Equal([EventKind.AgentReconnected, EventKind.HeartbeatLost], events!.Select(e => e.Kind));
    }

    [Fact]
    public async Task Gives_at_most_the_limit_the_newest_ones()
    {
        var (scenario, session) = await SeedSessionAsync();
        var ada = scenario.Students[0];
        for (var i = 1; i <= 4; i++)
            await AddEventAsync(session.Id, ada.Id, EventKind.UnblockRequest, $$"""{"host":"site{{i}}.example"}""", minutesAgo: i);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);
        var events = await client.GetFromJsonAsync<List<SessionRecentEvent>>(
            $"/sessions/{session.Id}/events?kind=UnblockRequest&limit=2");

        Assert.Equal(2, events!.Count);
        Assert.Contains("site1.example", events[0].PayloadJson);
        Assert.Contains("site2.example", events[1].PayloadJson);
    }

    [Fact]
    public async Task Leaves_out_another_sessions_events()
    {
        var (scenario, session) = await SeedSessionAsync();
        var other = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList(), ended: true);
        await AddEventAsync(other.Id, scenario.Students[0].Id, EventKind.ManualLeave, "{}", minutesAgo: 1);

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);
        var events = await client.GetFromJsonAsync<List<SessionRecentEvent>>($"/sessions/{session.Id}/events");

        Assert.Empty(events!);
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=501")]
    [InlineData("kind=NoSuchKind")]
    public async Task Returns_400_for_a_bad_query(string query)
    {
        var (scenario, session) = await SeedSessionAsync();

        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);
        var response = await client.GetAsync($"/sessions/{session.Id}/events?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<(TestScenario Scenario, Session Session)> SeedSessionAsync()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 2);
        var session = await TestSeed.AddSessionAsync(
            _factory, scenario.Teacher.Id, scenario.Class.Id, scenario.Students.Select(s => s.Id).ToList());
        return (scenario, session);
    }

    private async Task AddEventAsync(Guid sessionId, Guid userId, EventKind kind, string payloadJson, int minutesAgo)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        db.Events.Add(new Event
        {
            SessionId = sessionId,
            UserId = userId,
            Kind = kind,
            PayloadJson = payloadJson,
            OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
        });
        await db.SaveChangesAsync();
    }
}
