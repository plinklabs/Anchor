using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Anchor.Api.Controllers;
using Anchor.Api.Realtime;
using Anchor.Api.Tests.FakeAuth;
using Anchor.Domain.Bundles;
using Anchor.Domain.Classes;
using Anchor.Domain.Events;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Anchor.Api.Tests;

/// <summary>
/// Teachers are not participants of a session by default (#410). Starting one
/// used to send <c>SessionStarted</c> to the whole class, its teachers
/// included, so the agent and extension on a teacher's own laptop locked it
/// down the moment they started a session, before they could pick a bundle;
/// a co-teacher's extension joined with the code from the payload and showed
/// up in the roster. Now the start reaches the class's members only, and a
/// teacher takes part by choice, with the join code the live page shows.
/// These tests drive the hub as the teacher's agent, extension and live page
/// do, and check that every path a participant goes through accepts a teacher
/// who opted in.
/// </summary>
public sealed class SessionTeacherParticipationTests : IClassFixture<AnchorApiFactory>
{
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(5);

    /// <summary>Room for a message that shouldn't come to arrive anyway.</summary>
    private static readonly TimeSpan StrayDelivery = TimeSpan.FromMilliseconds(500);

    private readonly AnchorApiFactory _factory;

    public SessionTeacherParticipationTests(AnchorApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Starting_a_session_reaches_its_members_and_neither_its_teacher_nor_a_co_teacher()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 2);
        var coTeacher = await AddCoTeacherAsync(scenario);

        await using var owner = Connect(scenario.Teacher, "Teacher");
        await using var colleague = Connect(coTeacher, "Teacher");
        await using var student = Connect(scenario.Students[0], "Student");
        var ownerStarts = new Inbox<SessionStartedPayload>(owner, nameof(ISessionHubClient.SessionStarted));
        var colleagueStarts = new Inbox<SessionStartedPayload>(colleague, nameof(ISessionHubClient.SessionStarted));
        var studentStarts = new Inbox<SessionStartedPayload>(student, nameof(ISessionHubClient.SessionStarted));
        // In their user groups before the start, so a start sent to them would
        // reach them (#355).
        await owner.StartAndAwaitOnConnectedAsync();
        await colleague.StartAndAwaitOnConnectedAsync();
        await student.StartAndAwaitOnConnectedAsync();

        var started = await StartSessionAsync(scenario);

        await studentStarts.WaitForAsync(p => p.SessionId == started.Id, Arrival);
        await Task.Delay(StrayDelivery);
        Assert.Empty(ownerStarts.Received);
        Assert.Empty(colleagueStarts.Received);

        var broadcaster = _factory.Services.GetRequiredService<RecordingSessionBroadcaster>();
        var call = Assert.Single(broadcaster.SessionStartedCalls, c => c.SessionId == started.Id);
        Assert.Equal(
            scenario.Students.Select(s => s.Id).Order(),
            call.RecipientUserIds.Order());

        // Nothing makes them participants either: no row, nothing to catch up
        // on after a reconnect, no heartbeat accepted.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
            Assert.False(await db.SessionParticipants.AnyAsync(p =>
                p.SessionId == started.Id && (p.UserId == scenario.Teacher.Id || p.UserId == coTeacher.Id)));
        }
        foreach (var teacher in new[] { owner, colleague })
        {
            Assert.Null(await teacher.InvokeAsync<SessionStartedPayload?>("GetStartedSession"));
            Assert.False(await teacher.InvokeAsync<bool>("IsInSession", started.Id));
            var refused = await Assert.ThrowsAsync<HubException>(() => teacher.InvokeAsync("Heartbeat", started.Id));
            Assert.Contains("Not an active participant", refused.Message);
        }
    }

    [Fact]
    public async Task The_owning_teacher_takes_part_by_join_code_and_is_then_held_to_the_session_like_a_student()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 1);
        var bundle = await TestSeed.AddBundleAsync(_factory, "Bundle-" + Guid.NewGuid().ToString("N")[..6]);
        await TestSeed.AddBundleEntryAsync(
            _factory, bundle.Id, BundleEntryKind.Domain, "taking-part.example", BundleEntryMatchType.Suffix);
        var teacher = scenario.Teacher;

        // The teacher's live page and their own laptop's agent: two
        // connections of the same user.
        await using var livePage = Connect(teacher, "Teacher");
        await using var agent = Connect(teacher, "Teacher");
        var roster = new Inbox<ParticipantStateChangedPayload>(livePage, nameof(ISessionHubClient.ParticipantStateChanged));
        var starts = new Inbox<SessionStartedPayload>(agent, nameof(ISessionHubClient.SessionStarted));
        var bundleUpdates = new Inbox<SessionBundlesUpdatedPayload>(agent, nameof(ISessionHubClient.SessionBundlesUpdated));
        await livePage.StartAndAwaitOnConnectedAsync();
        await agent.StartAndAwaitOnConnectedAsync();

        var started = await StartSessionAsync(scenario, bundle.Id);
        await JoinAsync(livePage, started.Id);
        await Task.Delay(StrayDelivery);
        Assert.Empty(starts.Received);
        Assert.Null(await ParticipantAsync(started.Id, teacher.Id));

        // The teacher joins with the code their live page shows.
        using var client = TeacherClient(teacher);
        var join = await client.PostAsJsonAsync("/sessions/join-by-code", new JoinByCodeRequest(started.JoinCode));
        Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        var start = await starts.WaitForAsync(p => p.SessionId == started.Id, Arrival);
        Assert.Contains(start.Domains, d => d.Value == "taking-part.example");

        // Their agent confirms and joins, as it does on any SessionStarted:
        // JoinSession without a code, the owner's own session.
        await JoinAsync(agent, started.Id);
        Assert.NotNull((await ParticipantAsync(started.Id, teacher.Id))?.JoinedAt);
        // The roster lists them from now on, so it hears them join.
        var joined = await roster.WaitForAsync(p => p.UserId == teacher.Id, Arrival);
        Assert.Equal(nameof(ParticipantLiveState.Joined), joined.State);

        // Heartbeats from the agent and the extension, and the agent's events.
        await agent.InvokeAsync("Heartbeat", started.Id);
        await agent.InvokeAsync("ExtensionHeartbeat", started.Id);
        var tracker = _factory.Services.GetRequiredService<HeartbeatTracker>();
        Assert.True(tracker.TryGet(started.Id, teacher.Id, out var agentEntry, WitnessSource.Agent));
        Assert.False(agentEntry.AwaitingFirstPing); // pinged, not just awaited since the join (#444)
        Assert.True(tracker.TryGet(started.Id, teacher.Id, out _, WitnessSource.Extension));
        await agent.InvokeAsync("ReportEvent", new ReportEventRequest(
            started.Id, nameof(EventKind.ForegroundChange), """{"app":"powerpnt"}""", OccurredAt: null));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
            Assert.True(await db.Events.AnyAsync(e =>
                e.SessionId == started.Id && e.UserId == teacher.Id && e.Kind == EventKind.ForegroundChange));
        }

        // What a reconnecting agent or extension asks, and what a restarted
        // agent rejoins from.
        Assert.True(await agent.InvokeAsync<bool>("IsInSession", started.Id));
        Assert.Equal(started.Id, (await agent.InvokeAsync<SessionStartedPayload?>("GetStartedSession"))?.SessionId);
        var rejoinable = await client.GetFromJsonAsync<List<SessionStartedPayload>>("/sessions/rejoinable");
        Assert.Contains(rejoinable!, s => s.SessionId == started.Id);

        // The live page's roster shows them, in session.
        var detail = await client.GetFromJsonAsync<SessionDetailResponse>($"/sessions/{started.Id}");
        var row = Assert.Single(detail!.Participants, p => p.UserId == teacher.Id);
        Assert.Equal(nameof(ParticipantLiveState.Joined), row.State);

        // A bundle change mid-session reaches them like any active participant.
        var update = await client.PutAsJsonAsync(
            $"/sessions/{started.Id}/bundles", new UpdateSessionBundlesRequest(Array.Empty<Guid>()));
        update.EnsureSuccessStatusCode();
        var updated = await bundleUpdates.WaitForAsync(p => p.SessionId == started.Id, Arrival);
        Assert.DoesNotContain(updated.Domains, d => d.Value == "taking-part.example");
    }

    [Fact]
    public async Task A_co_teacher_takes_part_by_join_code()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 1);
        var coTeacher = await AddCoTeacherAsync(scenario);

        await using var colleague = Connect(coTeacher, "Teacher");
        var starts = new Inbox<SessionStartedPayload>(colleague, nameof(ISessionHubClient.SessionStarted));
        await colleague.StartAndAwaitOnConnectedAsync();

        var started = await StartSessionAsync(scenario);
        await Task.Delay(StrayDelivery);
        Assert.Empty(starts.Received);

        using var client = TeacherClient(coTeacher);
        var join = await client.PostAsJsonAsync("/sessions/join-by-code", new JoinByCodeRequest(started.JoinCode));
        Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        await starts.WaitForAsync(p => p.SessionId == started.Id, Arrival);

        await JoinAsync(colleague, started.Id);
        await colleague.InvokeAsync("Heartbeat", started.Id);
        Assert.True(_factory.Services.GetRequiredService<HeartbeatTracker>()
            .TryGet(started.Id, coTeacher.Id, out _));
        Assert.True(await colleague.InvokeAsync<bool>("IsInSession", started.Id));
        Assert.Equal(started.Id, (await colleague.InvokeAsync<SessionStartedPayload?>("GetStartedSession"))?.SessionId);
    }

    [Fact]
    public async Task The_owning_teachers_live_page_rejoining_brings_back_no_participation_they_left()
    {
        // The live page sends the same JoinSession as the teacher's agent, and
        // sends it again after every reconnect. It must not undo the teacher
        // leaving: a restarted agent would rejoin the session from
        // /sessions/rejoinable and lock their laptop down again.
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 1);
        var teacher = scenario.Teacher;
        await using var agent = Connect(teacher, "Teacher");
        await agent.StartAndAwaitOnConnectedAsync();
        var started = await StartSessionAsync(scenario);
        using var client = TeacherClient(teacher);
        (await client.PostAsJsonAsync("/sessions/join-by-code", new JoinByCodeRequest(started.JoinCode)))
            .EnsureSuccessStatusCode();
        await JoinAsync(agent, started.Id);
        await agent.InvokeAsync("LeaveSession", started.Id);

        await using (var livePage = Connect(teacher, "Teacher"))
        {
            await livePage.StartAndAwaitOnConnectedAsync();
            await JoinAsync(livePage, started.Id);
        }

        Assert.NotNull((await ParticipantAsync(started.Id, teacher.Id))?.LeftAt);
        Assert.False(await agent.InvokeAsync<bool>("IsInSession", started.Id));
        await Assert.ThrowsAsync<HubException>(() => agent.InvokeAsync("Heartbeat", started.Id));
        var rejoinable = await client.GetFromJsonAsync<List<SessionStartedPayload>>("/sessions/rejoinable");
        Assert.DoesNotContain(rejoinable!, s => s.SessionId == started.Id);

        // Taking part again is a new choice, with the code.
        (await client.PostAsJsonAsync("/sessions/join-by-code", new JoinByCodeRequest(started.JoinCode)))
            .EnsureSuccessStatusCode();
        await JoinAsync(agent, started.Id);
        Assert.True(await agent.InvokeAsync<bool>("IsInSession", started.Id));
    }

    [Fact]
    public async Task The_owning_teachers_live_page_doesnt_join_them_after_they_declined()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 1);
        var teacher = scenario.Teacher;
        await using var agent = Connect(teacher, "Teacher");
        await using var livePage = Connect(teacher, "Teacher");
        var roster = new Inbox<ParticipantStateChangedPayload>(livePage, nameof(ISessionHubClient.ParticipantStateChanged));
        await agent.StartAndAwaitOnConnectedAsync();
        await livePage.StartAndAwaitOnConnectedAsync();
        var started = await StartSessionAsync(scenario);
        await JoinAsync(livePage, started.Id);
        using var client = TeacherClient(teacher);
        (await client.PostAsJsonAsync("/sessions/join-by-code", new JoinByCodeRequest(started.JoinCode)))
            .EnsureSuccessStatusCode();

        // The teacher cancels the agent's join prompt, then the live page
        // reconnects and subscribes again.
        await agent.InvokeAsync("DeclineSession", new DeclineSessionRequest(started.Id, Reason: "user_cancelled"));
        await JoinAsync(livePage, started.Id);

        var participant = await ParticipantAsync(started.Id, teacher.Id);
        Assert.NotNull(participant?.DeclinedAt);
        Assert.Null(participant!.JoinedAt);
        Assert.False(await agent.InvokeAsync<bool>("IsInSession", started.Id));
        await Task.Delay(StrayDelivery);
        Assert.DoesNotContain(roster.Received, p =>
            p.UserId == teacher.Id && p.State == nameof(ParticipantLiveState.Joined));
    }

    // ------- Helpers -------

    private async Task<User> AddCoTeacherAsync(TestScenario scenario)
    {
        var coTeacher = await TestSeed.AddUserAsync(_factory, UserRole.Teacher, "Co-teacher " + Guid.NewGuid().ToString("N")[..6]);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        db.ClassMemberships.Add(new ClassMembership
        {
            ClassId = scenario.Class.Id,
            UserId = coTeacher.Id,
            Role = ClassMembershipRole.Teacher,
        });
        await db.SaveChangesAsync();
        return coTeacher;
    }

    private async Task<StartSessionResponse> StartSessionAsync(TestScenario scenario, params Guid[] bundleIds)
    {
        using var client = TeacherClient(scenario.Teacher);
        var response = await client.PostAsJsonAsync("/sessions", new StartSessionRequest(scenario.Class.Id, bundleIds));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StartSessionResponse>())!;
    }

    private HttpClient TeacherClient(User teacher)
    {
        var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, teacher);
        return client;
    }

    private async Task<Anchor.Domain.Sessions.SessionParticipant?> ParticipantAsync(Guid sessionId, Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        return await db.SessionParticipants.AsNoTracking()
            .SingleOrDefaultAsync(p => p.SessionId == sessionId && p.UserId == userId);
    }

    private static Task<JoinSessionResult> JoinAsync(HubConnection connection, Guid sessionId) =>
        connection.InvokeAsync<JoinSessionResult>(
            "JoinSession", new JoinSessionRequest(sessionId, JoinCode: null));

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

    /// <summary>The messages of one kind a connection receives.</summary>
    private sealed class Inbox<T>
    {
        private readonly ConcurrentQueue<T> _received = new();

        public Inbox(HubConnection connection, string method)
        {
            connection.On<T>(method, _received.Enqueue);
        }

        public IReadOnlyList<T> Received => _received.ToArray();

        public async Task<T> WaitForAsync(Func<T, bool> match, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                var found = _received.FirstOrDefault(match);
                if (found is not null)
                    return found;
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"No matching {typeof(T).Name} within {timeout}; received {_received.Count}.");
                await Task.Delay(20);
            }
        }
    }
}
