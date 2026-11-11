using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Anchor.Api.Controllers;
using Anchor.Api.Realtime;
using Anchor.Api.Tests.FakeAuth;
using Anchor.Domain.Events;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Anchor.Api.Tests;

/// <summary>
/// A student who stops taking part in a session that runs on is out of it, and
/// their Edge extension hears so (#462). Leaving, declining and quitting the
/// agent each told only the teacher's roster, so the extension went on
/// filtering against the session's allowlist and pinging it every 10 seconds,
/// and the hub logged each rejected ping as a failure. Now the hub sends
/// <c>ParticipationEnded</c> to the student's user group, where the extension
/// listens, and an extension that was offline then finds out from
/// <c>IsInSession</c> when it reconnects. Declining counts too, also after the
/// extension joined on <c>SessionStarted</c>, which it does without waiting for
/// the student's answer. Driven through real hub connections, as the student's
/// agent and extension and the teacher's live page make them.
/// </summary>
public sealed class SessionParticipationEndedTests : IClassFixture<AnchorApiFactory>
{
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(5);

    /// <summary>Room for a message that shouldn't come to arrive anyway.</summary>
    private static readonly TimeSpan StrayDelivery = TimeSpan.FromMilliseconds(500);

    private readonly AnchorApiFactory _factory;

    public SessionParticipationEndedTests(AnchorApiFactory factory)
    {
        _factory = factory;
    }

    /// <summary>The ways a student's agent takes them out of a running session.</summary>
    public enum Exit
    {
        /// <summary>"Leave session" in the agent's main window.</summary>
        Leave,

        /// <summary>The student cancels the agent's join prompt.</summary>
        Decline,

        /// <summary>An older agent quitting from the tray, which reports AgentKilled.</summary>
        QuitAgent,
    }

    [Theory]
    [InlineData(Exit.Leave)]
    [InlineData(Exit.Decline)]
    [InlineData(Exit.QuitAgent)]
    public async Task Leaving_the_session_tells_the_students_own_extension_and_takes_it_out_of_the_session(Exit exit)
    {
        var (sessionId, teacher, student, classmate) = await StartSessionAsync();

        await using var livePage = Connect(teacher, "Teacher");
        await using var agent = Connect(student, "Student");
        await using var extension = Connect(student, "Student");
        await using var classmateExtension = Connect(classmate, "Student");
        var livePageInbox = new Inbox(livePage);
        var agentInbox = new Inbox(agent);
        var extensionInbox = new Inbox(extension);
        var classmateInbox = new Inbox(classmateExtension);
        await livePage.StartAndAwaitOnConnectedAsync();
        await agent.StartAndAwaitOnConnectedAsync();
        await extension.StartAndAwaitOnConnectedAsync();
        await classmateExtension.StartAndAwaitOnConnectedAsync();
        await JoinAsync(livePage, sessionId);

        // The extension joins on SessionStarted and pings; the agent joins once
        // the student confirms, or declines instead.
        await JoinAsync(extension, sessionId);
        await JoinAsync(classmateExtension, sessionId);
        await extension.InvokeAsync(nameof(SessionHub.ExtensionHeartbeat), sessionId);
        await ExitAsync(agent, sessionId, exit);

        await extensionInbox.WaitForAsync(nameof(ISessionHubClient.ParticipationEnded), sessionId, Arrival);
        // The agent is in the same user group. It made the call itself and
        // ignores the message.
        await agentInbox.WaitForAsync(nameof(ISessionHubClient.ParticipationEnded), sessionId, Arrival);
        await Task.Delay(StrayDelivery);
        Assert.Empty(classmateInbox.Received);
        Assert.Empty(livePageInbox.Received);
        Assert.DoesNotContain(extensionInbox.Received, m => m.Method == nameof(ISessionHubClient.SessionEnded));
        var broadcaster = _factory.Services.GetRequiredService<RecordingSessionBroadcaster>();
        Assert.Equal(
            new[] { new ParticipationEndedCall(sessionId, student.Id) },
            broadcaster.ParticipationEndedCalls.Where(c => c.SessionId == sessionId).ToArray());

        // Out of the session: what the extension asks after a reconnect, and
        // a ping or a blocked URL already on its way, are turned away.
        Assert.False(await extension.InvokeAsync<bool>(nameof(SessionHub.IsInSession), sessionId));
        Assert.Null(await extension.InvokeAsync<SessionStartedPayload?>(nameof(SessionHub.GetStartedSession)));
        var ping = await Assert.ThrowsAsync<HubException>(() =>
            extension.InvokeAsync(nameof(SessionHub.ExtensionHeartbeat), sessionId));
        Assert.Contains("Not an active participant", ping.Message);
        await Assert.ThrowsAsync<HubException>(() => extension.InvokeAsync(
            nameof(SessionHub.ReportEvent),
            new ReportEventRequest(sessionId, nameof(EventKind.BlockedUrl), """{"url":"https://example.org/"}""", OccurredAt: null)));
        var tracker = _factory.Services.GetRequiredService<HeartbeatTracker>();
        Assert.False(tracker.TryGet(sessionId, student.Id, out _, WitnessSource.Extension));

        // The classmate is still in.
        Assert.True(await classmateExtension.InvokeAsync<bool>(nameof(SessionHub.IsInSession), sessionId));
        await classmateExtension.InvokeAsync(nameof(SessionHub.ExtensionHeartbeat), sessionId);
    }

    [Theory]
    [InlineData(Exit.Leave)]
    [InlineData(Exit.Decline)]
    [InlineData(Exit.QuitAgent)]
    public async Task Rejoining_by_code_brings_the_extension_back_into_the_session(Exit exit)
    {
        var (sessionId, _, student, _) = await StartSessionAsync();
        await using var agent = Connect(student, "Student");
        await using var extension = Connect(student, "Student");
        var extensionInbox = new Inbox(extension);
        await agent.StartAndAwaitOnConnectedAsync();
        await extension.StartAndAwaitOnConnectedAsync();
        await JoinAsync(extension, sessionId);
        await ExitAsync(agent, sessionId, exit);
        await extensionInbox.WaitForAsync(nameof(ISessionHubClient.ParticipationEnded), sessionId, Arrival);

        // "Join with code" in the agent: the start reaches the student's user
        // group again, and the extension joins on it, as on any SessionStarted.
        using var client = _factory.CreateClient();
        TestAuth.SetStudent(client, student);
        var join = await client.PostAsJsonAsync("/sessions/join-by-code", new JoinByCodeRequest(await JoinCodeAsync(sessionId)));
        Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        await extensionInbox.WaitForAsync(nameof(ISessionHubClient.SessionStarted), sessionId, Arrival);
        await JoinAsync(extension, sessionId);

        Assert.True(await extension.InvokeAsync<bool>(nameof(SessionHub.IsInSession), sessionId));
        Assert.Equal(sessionId, (await extension.InvokeAsync<SessionStartedPayload?>(nameof(SessionHub.GetStartedSession)))?.SessionId);
        await extension.InvokeAsync(nameof(SessionHub.ExtensionHeartbeat), sessionId);
        var tracker = _factory.Services.GetRequiredService<HeartbeatTracker>();
        Assert.True(tracker.TryGet(sessionId, student.Id, out _, WitnessSource.Extension));
    }

    [Fact]
    public async Task A_teacher_taking_part_who_leaves_keeps_their_live_page_open()
    {
        // A teacher who joined their own session by code (#410) has their
        // dashboard in the same user group as their agent and extension. What
        // tells the extension the teacher left must not read as the session
        // ending there, which would close the live page.
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 1);
        var teacher = scenario.Teacher;
        var sessionId = await StartSessionAsync(scenario);
        await using var livePage = Connect(teacher, "Teacher");
        await using var agent = Connect(teacher, "Teacher");
        var livePageInbox = new Inbox(livePage);
        await livePage.StartAndAwaitOnConnectedAsync();
        await agent.StartAndAwaitOnConnectedAsync();
        await JoinAsync(livePage, sessionId);
        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, teacher);
        (await client.PostAsJsonAsync("/sessions/join-by-code", new JoinByCodeRequest(await JoinCodeAsync(sessionId))))
            .EnsureSuccessStatusCode();
        await JoinAsync(agent, sessionId);

        await agent.InvokeAsync(nameof(SessionHub.LeaveSession), sessionId);

        await livePageInbox.WaitForAsync(nameof(ISessionHubClient.ParticipationEnded), sessionId, Arrival);
        await Task.Delay(StrayDelivery);
        Assert.DoesNotContain(livePageInbox.Received, m => m.Method == nameof(ISessionHubClient.SessionEnded));
    }

    [Fact]
    public async Task Leaving_a_session_without_a_participant_row_tells_no_one()
    {
        // StartAndAwaitOnConnectedAsync leaves a session that doesn't exist to
        // prove a connection is in its user group; that must stay silent.
        var (_, _, student, _) = await StartSessionAsync();
        await using var extension = Connect(student, "Student");
        var extensionInbox = new Inbox(extension);
        await extension.StartAndAwaitOnConnectedAsync();
        var nowhere = Guid.NewGuid();

        await extension.InvokeAsync(nameof(SessionHub.LeaveSession), nowhere);

        await Task.Delay(StrayDelivery);
        Assert.Empty(extensionInbox.Received);
        var broadcaster = _factory.Services.GetRequiredService<RecordingSessionBroadcaster>();
        Assert.DoesNotContain(broadcaster.ParticipationEndedCalls, c => c.UserId == student.Id);
    }

    // ------- Helpers -------

    private static async Task ExitAsync(HubConnection agent, Guid sessionId, Exit exit)
    {
        switch (exit)
        {
            case Exit.Leave:
                // SessionCoordinator.LeaveSessionManuallyAsync: report, then leave.
                await JoinAsync(agent, sessionId);
                await agent.InvokeAsync(nameof(SessionHub.ReportEvent), new ReportEventRequest(
                    sessionId, nameof(EventKind.ManualLeave), "{}", OccurredAt: null));
                await agent.InvokeAsync(nameof(SessionHub.LeaveSession), sessionId);
                break;
            case Exit.Decline:
                await agent.InvokeAsync(nameof(SessionHub.DeclineSession), new DeclineSessionRequest(sessionId, "user_cancelled"));
                break;
            case Exit.QuitAgent:
                await JoinAsync(agent, sessionId);
                await agent.InvokeAsync(nameof(SessionHub.ReportEvent), new ReportEventRequest(
                    sessionId, nameof(EventKind.AgentKilled), "{}", OccurredAt: null));
                break;
        }
    }

    private async Task<(Guid SessionId, User Teacher, User Student, User Classmate)> StartSessionAsync()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 2);
        return (await StartSessionAsync(scenario), scenario.Teacher, scenario.Students[0], scenario.Students[1]);
    }

    private async Task<Guid> StartSessionAsync(TestScenario scenario)
    {
        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);
        var response = await client.PostAsJsonAsync("/sessions", new StartSessionRequest(scenario.Class.Id, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StartSessionResponse>())!.Id;
    }

    private async Task<string> JoinCodeAsync(Guid sessionId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        return (await db.Sessions.FindAsync(sessionId))!.JoinCode;
    }

    private static Task<JoinSessionResult> JoinAsync(HubConnection connection, Guid sessionId) =>
        connection.InvokeAsync<JoinSessionResult>(
            nameof(SessionHub.JoinSession), new JoinSessionRequest(sessionId, JoinCode: null));

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

    /// <summary>
    /// What a connection hears that is about a whole session, as (message,
    /// session id): the messages an agent or extension acts on, and
    /// <c>ParticipationEnded</c>.
    /// </summary>
    private sealed class Inbox
    {
        private readonly ConcurrentQueue<(string Method, Guid SessionId)> _received = new();

        public Inbox(HubConnection connection)
        {
            connection.On<Guid>(nameof(ISessionHubClient.ParticipationEnded),
                id => _received.Enqueue((nameof(ISessionHubClient.ParticipationEnded), id)));
            connection.On<Guid>(nameof(ISessionHubClient.SessionEnded),
                id => _received.Enqueue((nameof(ISessionHubClient.SessionEnded), id)));
            connection.On<SessionStartedPayload>(nameof(ISessionHubClient.SessionStarted),
                p => _received.Enqueue((nameof(ISessionHubClient.SessionStarted), p.SessionId)));
        }

        public IReadOnlyList<(string Method, Guid SessionId)> Received => _received.ToArray();

        public async Task WaitForAsync(string method, Guid sessionId, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!_received.Contains((method, sessionId)))
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException(
                        $"No {method} for {sessionId} within {timeout}; received [{string.Join(", ", Received)}].");
                await Task.Delay(20);
            }
        }
    }
}
