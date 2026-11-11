using System.Collections.Concurrent;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Anchor.Api.Tests;

/// <summary>
/// A student who stops taking part reaches the teacher's roster as that, and
/// as nothing more (#448). Their agent and extension stop pinging, and that
/// silence used to turn into a <c>HeartbeatLost</c> and an
/// <c>extension_silent</c> tamper flag some 20 seconds after a normal "Leave
/// session", which looks like a laptop that dropped out. Driven through real
/// hub connections, as the student's agent and extension and the teacher's
/// live page make them, with the heartbeat monitor scanning the tracker the hub
/// writes into once the timeout has passed.
/// </summary>
public sealed class SessionLeaveHeartbeatTests : IClassFixture<SessionLeaveHeartbeatTests.LeaveTestFactory>
{
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(5);

    private readonly LeaveTestFactory _factory;

    public SessionLeaveHeartbeatTests(LeaveTestFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Own factory, so the heartbeat tracker the monitor scans here holds only
    /// what these tests put in it.
    /// </summary>
    public sealed class LeaveTestFactory : AnchorApiFactory { }

    [Fact]
    public async Task Leaving_reaches_the_teacher_as_Left_and_never_as_a_lost_agent_or_a_silent_extension()
    {
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

        // The agent and the extension each join on SessionStarted, and each pings.
        await JoinAsync(agent, sessionId);
        await JoinAsync(extension, sessionId);
        await agent.InvokeAsync(nameof(SessionHub.Heartbeat), sessionId);
        await extension.InvokeAsync(nameof(SessionHub.ExtensionHeartbeat), sessionId);

        // "Leave session" in the agent's main window: it reports ManualLeave,
        // then leaves (SessionCoordinator.LeaveSessionManuallyAsync).
        await agent.InvokeAsync(nameof(SessionHub.ReportEvent), new ReportEventRequest(
            sessionId, nameof(EventKind.ManualLeave), "{}", OccurredAt: null));
        await agent.InvokeAsync(nameof(SessionHub.LeaveSession), sessionId);

        var tracker = _factory.Services.GetRequiredService<HeartbeatTracker>();
        Assert.False(tracker.TryGet(sessionId, student.Id, out _, WitnessSource.Agent));
        Assert.False(tracker.TryGet(sessionId, student.Id, out _, WitnessSource.Extension));

        // A ping the extension sends before it hears it is out of the session
        // (ParticipationEnded, #462) is turned away.
        var rejected = await Assert.ThrowsAsync<HubException>(() =>
            extension.InvokeAsync(nameof(SessionHub.ExtensionHeartbeat), sessionId));
        Assert.Contains("Not an active participant", rejected.Message);

        await ScanPastTheTimeoutAsync();
        await AwaitEverythingSentSoFarAsync(feed, classmateAgent, sessionId, classmate);

        // The teacher's live roster: the joins, then Left, and nothing after.
        Assert.Equal(
            new[] { "ParticipantStateChanged:Joined", "ParticipantStateChanged:Joined", "ParticipantStateChanged:Left" },
            feed.About(student.Id));

        // The roster the live page loads, and the session's stored events.
        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, teacher);
        var detail = await client.GetFromJsonAsync<SessionDetailResponse>($"/sessions/{sessionId}");
        var row = Assert.Single(detail!.Participants, p => p.UserId == student.Id);
        Assert.Equal(nameof(ParticipantLiveState.Left), row.State);
        Assert.False(row.Tampered);
        Assert.Equal(
            new[] { EventKind.ManualLeave },
            detail.RecentEvents.Where(e => e.UserId == student.Id).Select(e => e.Kind).ToArray());
    }

    [Fact]
    public async Task Declining_a_session_the_agent_was_in_reaches_the_teacher_as_Declined_and_never_as_a_lost_agent()
    {
        // A restarted agent whose silent rejoin failed has its catch-up offer
        // the session again (#356), and the student declines it.
        var (sessionId, teacher, student, classmate) = await StartSessionAsync();

        await using var livePage = Connect(teacher, "Teacher");
        await using var agent = Connect(student, "Student");
        await using var classmateAgent = Connect(classmate, "Student");
        var feed = new RosterFeed(livePage);
        await livePage.StartAndAwaitOnConnectedAsync();
        await JoinAsync(livePage, sessionId);
        await agent.StartAndAwaitOnConnectedAsync();
        await classmateAgent.StartAndAwaitOnConnectedAsync();

        await JoinAsync(agent, sessionId);
        await agent.InvokeAsync(nameof(SessionHub.Heartbeat), sessionId);
        await agent.InvokeAsync(nameof(SessionHub.DeclineSession), new DeclineSessionRequest(sessionId, "user_cancelled"));

        await ScanPastTheTimeoutAsync();
        await AwaitEverythingSentSoFarAsync(feed, classmateAgent, sessionId, classmate);

        Assert.Equal(
            new[] { "ParticipantStateChanged:Joined", "ParticipantStateChanged:Declined" },
            feed.About(student.Id));
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnchorDbContext>();
        Assert.Equal(
            new[] { EventKind.JoinDeclined },
            await db.Events.AsNoTracking()
                .Where(e => e.SessionId == sessionId && e.UserId == student.Id)
                .Select(e => e.Kind)
                .ToArrayAsync());
    }

    /// <summary>
    /// One heartbeat-monitor pass at a time well past the timeout, over the
    /// tracker the hub writes into.
    /// </summary>
    private async Task ScanPastTheTimeoutAsync()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddMinutes(5));
        var monitor = ActivatorUtilities.CreateInstance<HeartbeatMonitor>(_factory.Services, (TimeProvider)clock);
        await monitor.ScanOnceAsync(CancellationToken.None);
    }

    /// <summary>
    /// A connection receives its messages in the order they were sent, so once
    /// a classmate's join, sent now, has reached the teacher, so has anything
    /// sent to them before it, such as what the monitor broadcast.
    /// </summary>
    private static async Task AwaitEverythingSentSoFarAsync(
        RosterFeed feed, HubConnection classmateAgent, Guid sessionId, User classmate)
    {
        await JoinAsync(classmateAgent, sessionId);
        await feed.WaitForAsync(classmate.Id, "ParticipantStateChanged:Joined", Arrival);
    }

    private async Task<(Guid SessionId, User Teacher, User Student, User Classmate)> StartSessionAsync()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 2);
        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);
        var response = await client.PostAsJsonAsync("/sessions", new StartSessionRequest(scenario.Class.Id, null));
        response.EnsureSuccessStatusCode();
        var started = await response.Content.ReadFromJsonAsync<StartSessionResponse>();
        return (started!.Id, scenario.Teacher, scenario.Students[0], scenario.Students[1]);
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
    /// The roster signals the teacher's live page receives, as (student,
    /// signal), where a signal is the message name, or
    /// <c>ParticipantStateChanged:{state}</c>.
    /// </summary>
    private sealed class RosterFeed
    {
        private readonly ConcurrentQueue<(Guid UserId, string Signal)> _received = new();

        public RosterFeed(HubConnection connection)
        {
            connection.On<ParticipantStateChangedPayload>(nameof(ISessionHubClient.ParticipantStateChanged),
                p => _received.Enqueue((p.UserId, $"{nameof(ISessionHubClient.ParticipantStateChanged)}:{p.State}")));
            connection.On<TamperDetectedPayload>(nameof(ISessionHubClient.TamperDetected),
                p => _received.Enqueue((p.UserId, $"{nameof(ISessionHubClient.TamperDetected)}:{p.Kind}")));
            connection.On<HeartbeatLostPayload>(nameof(ISessionHubClient.HeartbeatLost),
                p => _received.Enqueue((p.UserId, nameof(ISessionHubClient.HeartbeatLost))));
            connection.On<AgentReconnectedPayload>(nameof(ISessionHubClient.AgentReconnected),
                p => _received.Enqueue((p.UserId, nameof(ISessionHubClient.AgentReconnected))));
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
