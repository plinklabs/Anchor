using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Anchor.Api.Controllers;
using Anchor.Api.Realtime;
using Anchor.Api.Tests.FakeAuth;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Anchor.Api.Tests;

/// <summary>
/// A student who declines a session after joining it reaches the teacher's
/// roster as Declined, in the live push and in the roster the live page loads
/// on it (#471). The Edge extension joins on <c>SessionStarted</c> without
/// waiting for the student's answer in the agent, so their decline lands on a
/// row it already joined. The roster ranked a join above a decline, so the
/// student's row flashed Declined on the push and turned back to Joined once
/// the dashboard re-fetched the roster, while the rest of the backend counted
/// them out of the session (#462). Driven through real hub connections, as the
/// student's agent and extension and the teacher's live page make them.
/// </summary>
public sealed class SessionDeclineAfterJoinTests : IClassFixture<AnchorApiFactory>
{
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(5);

    private readonly AnchorApiFactory _factory;

    public SessionDeclineAfterJoinTests(AnchorApiFactory factory)
    {
        _factory = factory;
    }

    /// <summary>What joined the session before the student declined it.</summary>
    public enum FirstJoin
    {
        /// <summary>The extension, on SessionStarted, while the agent's join prompt shows.</summary>
        Extension,

        /// <summary>
        /// The agent: a restarted agent whose silent rejoin failed has its
        /// catch-up offer the session again (#356).
        /// </summary>
        Agent,
    }

    [Theory]
    [InlineData(FirstJoin.Extension)]
    [InlineData(FirstJoin.Agent)]
    public async Task Declining_after_joining_reads_as_Declined_on_the_live_roster_in_the_roster_it_loads_and_after_the_session(FirstJoin first)
    {
        var (sessionId, teacher, student, _) = await StartSessionAsync();
        await using var livePage = Connect(teacher, "Teacher");
        await using var agent = Connect(student, "Student");
        await using var extension = Connect(student, "Student");
        var feed = new RosterFeed(livePage);
        await livePage.StartAndAwaitOnConnectedAsync();
        await JoinAsync(livePage, sessionId);
        await agent.StartAndAwaitOnConnectedAsync();
        await extension.StartAndAwaitOnConnectedAsync();

        if (first == FirstJoin.Extension)
        {
            await JoinAsync(extension, sessionId);
            await extension.InvokeAsync(nameof(SessionHub.ExtensionHeartbeat), sessionId);
        }
        else
        {
            await JoinAsync(agent, sessionId);
            await agent.InvokeAsync(nameof(SessionHub.Heartbeat), sessionId);
        }
        await feed.WaitForAsync(student.Id, "ParticipantStateChanged:Joined", Arrival);
        Assert.Equal(nameof(ParticipantLiveState.Joined), (await RosterRowAsync(teacher, sessionId, student.Id)).State);

        // The student cancels the agent's join prompt.
        await agent.InvokeAsync(nameof(SessionHub.DeclineSession), new DeclineSessionRequest(sessionId, "user_cancelled"));

        // The live page re-fetches the roster on each ParticipantStateChanged.
        await feed.WaitForAsync(student.Id, "ParticipantStateChanged:Declined", Arrival);
        var row = await RosterRowAsync(teacher, sessionId, student.Id);
        Assert.Equal(nameof(ParticipantLiveState.Declined), row.State);
        Assert.NotNull(row.JoinedAt);
        Assert.NotNull(row.DeclinedAt);
        Assert.Null(row.LeftAt);
        Assert.Equal(
            new[] { "ParticipantStateChanged:Joined", "ParticipantStateChanged:Declined" },
            feed.About(student.Id));

        // The session's review, which the past-session page loads, says the same.
        using var client = TeacherClient(teacher);
        (await client.PostAsync($"/sessions/{sessionId}/end", content: null)).EnsureSuccessStatusCode();
        Assert.Equal(nameof(ParticipantLiveState.Declined), (await RosterRowAsync(teacher, sessionId, student.Id)).State);
    }

    [Fact]
    public async Task A_join_that_arrives_after_the_decline_leaves_the_student_out_and_Declined()
    {
        // The student's answer in the agent reaches the backend before the
        // extension's join on the same SessionStarted.
        var (sessionId, teacher, student, classmate) = await StartSessionAsync();
        await using var livePage = Connect(teacher, "Teacher");
        await using var agent = Connect(student, "Student");
        await using var extension = Connect(student, "Student");
        await using var classmateAgent = Connect(classmate, "Student");
        var feed = new RosterFeed(livePage);
        await livePage.StartAndAwaitOnConnectedAsync();
        await JoinAsync(livePage, sessionId);
        await agent.StartAndAwaitOnConnectedAsync();
        await extension.StartAndAwaitOnConnectedAsync();
        await classmateAgent.StartAndAwaitOnConnectedAsync();

        await agent.InvokeAsync(nameof(SessionHub.DeclineSession), new DeclineSessionRequest(sessionId, "user_cancelled"));
        var refused = await Assert.ThrowsAsync<HubException>(() => JoinAsync(extension, sessionId));
        Assert.Contains("declined", refused.Message, StringComparison.OrdinalIgnoreCase);

        // A connection receives its messages in the order they were sent, so
        // once the classmate's join has reached the live page, anything the
        // extension's join sent would have too.
        await JoinAsync(classmateAgent, sessionId);
        await feed.WaitForAsync(classmate.Id, "ParticipantStateChanged:Joined", Arrival);
        Assert.Equal(new[] { "ParticipantStateChanged:Declined" }, feed.About(student.Id));

        var row = await RosterRowAsync(teacher, sessionId, student.Id);
        Assert.Equal(nameof(ParticipantLiveState.Declined), row.State);
        Assert.Null(row.JoinedAt);
        Assert.False(await extension.InvokeAsync<bool>(nameof(SessionHub.IsInSession), sessionId));
        // Nothing waits for an agent the student turned down, so no missing
        // agent or lost heartbeat follows.
        var tracker = _factory.Services.GetRequiredService<HeartbeatTracker>();
        Assert.False(tracker.TryGet(sessionId, student.Id, out _));
    }

    [Fact]
    public async Task Joining_again_by_code_after_declining_reads_as_Joined()
    {
        var (sessionId, teacher, student, _) = await StartSessionAsync();
        await using var livePage = Connect(teacher, "Teacher");
        await using var agent = Connect(student, "Student");
        await using var extension = Connect(student, "Student");
        var feed = new RosterFeed(livePage);
        await livePage.StartAndAwaitOnConnectedAsync();
        await JoinAsync(livePage, sessionId);
        await agent.StartAndAwaitOnConnectedAsync();
        await extension.StartAndAwaitOnConnectedAsync();
        await JoinAsync(extension, sessionId);
        await agent.InvokeAsync(nameof(SessionHub.DeclineSession), new DeclineSessionRequest(sessionId, "user_cancelled"));
        await feed.WaitForAsync(student.Id, "ParticipantStateChanged:Declined", Arrival);

        // "Join with code" in the agent clears the decline and sends the start
        // again: the extension joins on it, and the agent once the student
        // confirms its prompt.
        using var studentClient = _factory.CreateClient();
        TestAuth.SetStudent(studentClient, student);
        var join = await studentClient.PostAsJsonAsync("/sessions/join-by-code", new JoinByCodeRequest(await JoinCodeAsync(sessionId)));
        Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        await JoinAsync(extension, sessionId);
        await JoinAsync(agent, sessionId);
        await agent.InvokeAsync(nameof(SessionHub.Heartbeat), sessionId);

        await WaitForCountAsync(feed, student.Id, "ParticipantStateChanged:Joined", 3);
        Assert.Equal(
            new[]
            {
                "ParticipantStateChanged:Joined",
                "ParticipantStateChanged:Declined",
                "ParticipantStateChanged:Joined",
                "ParticipantStateChanged:Joined",
            },
            feed.About(student.Id));
        var row = await RosterRowAsync(teacher, sessionId, student.Id);
        Assert.Equal(nameof(ParticipantLiveState.Joined), row.State);
        Assert.Null(row.DeclinedAt);
        Assert.True(await extension.InvokeAsync<bool>(nameof(SessionHub.IsInSession), sessionId));
    }

    // ------- Helpers -------

    private async Task<(Guid SessionId, User Teacher, User Student, User Classmate)> StartSessionAsync()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 2);
        using var client = TeacherClient(scenario.Teacher);
        var response = await client.PostAsJsonAsync("/sessions", new StartSessionRequest(scenario.Class.Id, null));
        response.EnsureSuccessStatusCode();
        var started = await response.Content.ReadFromJsonAsync<StartSessionResponse>();
        return (started!.Id, scenario.Teacher, scenario.Students[0], scenario.Students[1]);
    }

    private HttpClient TeacherClient(User teacher)
    {
        var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, teacher);
        return client;
    }

    /// <summary>The student's row in the roster the teacher's live page loads.</summary>
    private async Task<SessionParticipantSummary> RosterRowAsync(User teacher, Guid sessionId, Guid userId)
    {
        using var client = TeacherClient(teacher);
        var detail = await client.GetFromJsonAsync<SessionDetailResponse>($"/sessions/{sessionId}");
        return Assert.Single(detail!.Participants, p => p.UserId == userId);
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

    private static async Task WaitForCountAsync(RosterFeed feed, Guid userId, string signal, int count)
    {
        var deadline = DateTime.UtcNow + Arrival;
        while (feed.About(userId).Count(s => s == signal) < count)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    $"Fewer than {count} {signal} for {userId} within {Arrival}; received [{string.Join(", ", feed.About(userId))}].");
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// The roster pushes the teacher's live page receives, as (student,
    /// <c>ParticipantStateChanged:{state}</c>).
    /// </summary>
    private sealed class RosterFeed
    {
        private readonly ConcurrentQueue<(Guid UserId, string Signal)> _received = new();

        public RosterFeed(HubConnection connection)
        {
            connection.On<ParticipantStateChangedPayload>(nameof(ISessionHubClient.ParticipantStateChanged),
                p => _received.Enqueue((p.UserId, $"{nameof(ISessionHubClient.ParticipantStateChanged)}:{p.State}")));
        }

        public string[] About(Guid userId) =>
            _received.Where(r => r.UserId == userId).Select(r => r.Signal).ToArray();

        public async Task WaitForAsync(Guid userId, string signal, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!About(userId).Contains(signal))
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"No {signal} for {userId} within {timeout}; received [{string.Join(", ", About(userId))}].");
                await Task.Delay(20);
            }
        }
    }
}
