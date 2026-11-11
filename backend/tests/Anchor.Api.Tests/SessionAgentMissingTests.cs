using System.Collections.Concurrent;
using System.Net.Http.Json;
using Anchor.Api.Controllers;
using Anchor.Api.Realtime;
using Anchor.Api.Tests.FakeAuth;
using Anchor.Domain.Events;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Anchor.Api.Tests;

/// <summary>
/// A student whose agent never takes part reaches the teacher's roster as that
/// (#444). The agent and the Edge extension each join the session, so an
/// extension that joined on its own used to read as a healthy join for the whole
/// lesson, while nothing blocked apps on that laptop. Driven through real hub
/// connections, as the student's extension and agent and the teacher's live
/// page make them, on a fake clock the whole backend reads, with the heartbeat
/// monitor scanning the tracker the hub writes into.
/// </summary>
public sealed class SessionAgentMissingTests : IClassFixture<SessionAgentMissingTests.AgentMissingTestFactory>
{
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(5);

    private readonly AgentMissingTestFactory _factory;

    public SessionAgentMissingTests(AgentMissingTestFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Own factory, so the tracker the monitor scans holds only these tests'
    /// sessions, and the hub, the roster and the monitor all read one clock the
    /// tests move.
    /// </summary>
    public sealed class AgentMissingTestFactory : AnchorApiFactory
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

    [Fact]
    public async Task An_extension_that_joins_without_its_agent_reaches_the_teacher_as_agent_missing_until_the_agent_turns_up()
    {
        var (sessionId, teacher, student, absent) = await StartSessionAsync();

        await using var livePage = Connect(teacher, "Teacher");
        await using var extension = Connect(student, "Student");
        var feed = new RosterFeed(livePage);
        await livePage.StartAndAwaitOnConnectedAsync();
        await JoinAsync(livePage, sessionId);
        await extension.StartAndAwaitOnConnectedAsync();

        // SessionStarted reaches the extension, which joins, and pings every
        // 10 seconds from then on. The student's agent isn't running.
        await JoinAsync(extension, sessionId);

        // 25s in: past the 20s heartbeat timeout, but an agent may still be on
        // its join toast or waiting to send its first ping.
        await PassAsync(TimeSpan.FromSeconds(25), extension, sessionId);
        Assert.Equal(new[] { nameof(ParticipantLiveState.Joined) }, PushedStates(sessionId, student.Id));
        Assert.Equal(nameof(ParticipantLiveState.Joined), (await RosterRowAsync(teacher, sessionId, student.Id)).State);

        // 35s in: past the 30s join grace without a single agent heartbeat.
        await PassAsync(TimeSpan.FromSeconds(10), extension, sessionId);
        await ScanAsync(); // one report, not one per scan
        await feed.WaitForAsync(student.Id, "ParticipantStateChanged:AgentMissing", Arrival);
        var missing = await RosterRowAsync(teacher, sessionId, student.Id);
        Assert.Equal(nameof(ParticipantLiveState.AgentMissing), missing.State);
        Assert.False(missing.Tampered);
        // Nothing joined for this classmate: still not joined, never missing.
        Assert.Equal(nameof(ParticipantLiveState.NeverJoined), (await RosterRowAsync(teacher, sessionId, absent.Id)).State);
        Assert.Empty(PushedStates(sessionId, absent.Id));

        // The agent starts after all: it joins, and pings an interval later.
        await using var agent = Connect(student, "Student");
        await agent.StartAndAwaitOnConnectedAsync();
        await JoinAsync(agent, sessionId);
        // Its join alone doesn't show it's there yet; its heartbeat does.
        Assert.Equal(nameof(ParticipantLiveState.AgentMissing), (await RosterRowAsync(teacher, sessionId, student.Id)).State);
        _factory.Clock.Advance(TimeSpan.FromSeconds(10));
        await agent.InvokeAsync(nameof(SessionHub.Heartbeat), sessionId);
        await extension.InvokeAsync(nameof(SessionHub.ExtensionHeartbeat), sessionId);
        await ScanAsync();
        await feed.WaitForAsync(student.Id, nameof(ISessionHubClient.AgentReconnected), Arrival);
        Assert.Equal(nameof(ParticipantLiveState.Joined), (await RosterRowAsync(teacher, sessionId, student.Id)).State);

        // An agent that was there and goes silent is a lost heartbeat, as before.
        await PassAsync(TimeSpan.FromSeconds(25), extension, sessionId);
        await feed.WaitForAsync(student.Id, nameof(ISessionHubClient.HeartbeatLost), Arrival);
        Assert.Equal(nameof(ParticipantLiveState.HeartbeatStale), (await RosterRowAsync(teacher, sessionId, student.Id)).State);

        // What the live page received, in order: the extension's join, the
        // missing agent, the agent's join, its arrival, and its silence.
        Assert.Equal(
            new[]
            {
                "ParticipantStateChanged:Joined",
                "ParticipantStateChanged:AgentMissing",
                "ParticipantStateChanged:Joined",
                "AgentReconnected",
                "HeartbeatLost",
            },
            feed.About(student.Id));
        // And what the session keeps, which its review shows after it ends.
        Assert.Equal(
            new[] { EventKind.AgentMissing, EventKind.AgentReconnected, EventKind.HeartbeatLost },
            await EventKindsAsync(sessionId, student.Id));

        await EndSessionAsync(teacher, sessionId);
    }

    [Fact]
    public async Task A_student_whose_agent_and_extension_both_join_never_reads_as_agent_missing()
    {
        // The usual start: the extension joins at once, the agent after its
        // 5-second join toast, and each pings every 10 seconds.
        var (sessionId, teacher, student, _) = await StartSessionAsync();

        await using var livePage = Connect(teacher, "Teacher");
        await using var extension = Connect(student, "Student");
        await using var agent = Connect(student, "Student");
        var feed = new RosterFeed(livePage);
        await livePage.StartAndAwaitOnConnectedAsync();
        await JoinAsync(livePage, sessionId);
        await extension.StartAndAwaitOnConnectedAsync();
        await agent.StartAndAwaitOnConnectedAsync();

        await JoinAsync(extension, sessionId);
        _factory.Clock.Advance(TimeSpan.FromSeconds(5));
        await JoinAsync(agent, sessionId);
        for (var second = 10; second <= 90; second += 5)
        {
            _factory.Clock.Advance(TimeSpan.FromSeconds(5));
            if (second % 10 == 0)
                await extension.InvokeAsync(nameof(SessionHub.ExtensionHeartbeat), sessionId);
            else
                await agent.InvokeAsync(nameof(SessionHub.Heartbeat), sessionId);
            await ScanAsync();
        }

        Assert.Equal(nameof(ParticipantLiveState.Joined), (await RosterRowAsync(teacher, sessionId, student.Id)).State);
        Assert.DoesNotContain(PushedStates(sessionId, student.Id), s => s != nameof(ParticipantLiveState.Joined));
        Assert.Empty(await EventKindsAsync(sessionId, student.Id));

        // The agent leaving the usual way ends it there: no missing agent after.
        await agent.InvokeAsync(nameof(SessionHub.LeaveSession), sessionId);
        _factory.Clock.Advance(TimeSpan.FromMinutes(2));
        await ScanAsync();
        await feed.WaitForAsync(student.Id, "ParticipantStateChanged:Left", Arrival);
        Assert.Equal(
            new[] { "ParticipantStateChanged:Joined", "ParticipantStateChanged:Joined", "ParticipantStateChanged:Left" },
            feed.About(student.Id));
        Assert.Empty(await EventKindsAsync(sessionId, student.Id));

        await EndSessionAsync(teacher, sessionId);
    }

    [Fact]
    public async Task After_a_backend_restart_an_extension_that_keeps_pinging_still_reveals_a_missing_agent()
    {
        // A restart empties the heartbeat tracker. The extension reconnects and
        // keeps pinging without joining again, so its pings start the wait.
        var (sessionId, teacher, student, _) = await StartSessionAsync();

        await using var livePage = Connect(teacher, "Teacher");
        await using var extension = Connect(student, "Student");
        var feed = new RosterFeed(livePage);
        await livePage.StartAndAwaitOnConnectedAsync();
        await JoinAsync(livePage, sessionId);
        await extension.StartAndAwaitOnConnectedAsync();
        await JoinAsync(extension, sessionId);

        _factory.Services.GetRequiredService<HeartbeatTracker>().ClearSession(sessionId);

        await extension.InvokeAsync(nameof(SessionHub.ExtensionHeartbeat), sessionId);
        await PassAsync(TimeSpan.FromSeconds(25), extension, sessionId);
        Assert.DoesNotContain(PushedStates(sessionId, student.Id), s => s != nameof(ParticipantLiveState.Joined));
        await PassAsync(TimeSpan.FromSeconds(10), extension, sessionId);

        await feed.WaitForAsync(student.Id, "ParticipantStateChanged:AgentMissing", Arrival);
        Assert.Equal(nameof(ParticipantLiveState.AgentMissing), (await RosterRowAsync(teacher, sessionId, student.Id)).State);

        await EndSessionAsync(teacher, sessionId);
    }

    /// <summary>
    /// Moves the clock on in 5-second steps, the monitor's scan interval, and
    /// scans after each, with the extension pinging every 10 seconds, as it
    /// does: its first ping comes an interval after it joined, or after the
    /// ping before this call.
    /// </summary>
    private async Task PassAsync(TimeSpan span, HubConnection extension, Guid sessionId)
    {
        var step = TimeSpan.FromSeconds(5);
        for (var passed = step; passed <= span; passed += step)
        {
            _factory.Clock.Advance(step);
            if (passed.TotalSeconds % 10 == 0)
                await extension.InvokeAsync(nameof(SessionHub.ExtensionHeartbeat), sessionId);
            await ScanAsync();
        }
    }

    /// <summary>One heartbeat-monitor pass over the tracker the hub writes into.</summary>
    private Task ScanAsync() =>
        ActivatorUtilities.CreateInstance<HeartbeatMonitor>(_factory.Services).ScanOnceAsync(CancellationToken.None);

    /// <summary>
    /// The states pushed to the student's teacher so far, as the broadcaster
    /// sent them: synchronously with the monitor's scan, so it also shows what
    /// wasn't sent.
    /// </summary>
    private string[] PushedStates(Guid sessionId, Guid userId) =>
        _factory.Services.GetRequiredService<RecordingSessionBroadcaster>().ParticipantStateChangedCalls
            .Where(c => c.SessionId == sessionId && c.UserId == userId)
            .Select(c => c.State)
            .ToArray();

    /// <summary>The student's row on the roster the live page loads.</summary>
    private async Task<SessionParticipantSummary> RosterRowAsync(User teacher, Guid sessionId, Guid userId)
    {
        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, teacher);
        var detail = await client.GetFromJsonAsync<SessionDetailResponse>($"/sessions/{sessionId}");
        return Assert.Single(detail!.Participants, p => p.UserId == userId);
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

    private async Task<(Guid SessionId, User Teacher, User Student, User Absent)> StartSessionAsync()
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount: 2);
        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);
        var response = await client.PostAsJsonAsync("/sessions", new StartSessionRequest(scenario.Class.Id, null));
        response.EnsureSuccessStatusCode();
        var started = await response.Content.ReadFromJsonAsync<StartSessionResponse>();
        return (started!.Id, scenario.Teacher, scenario.Students[0], scenario.Students[1]);
    }

    private async Task EndSessionAsync(User teacher, Guid sessionId)
    {
        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, teacher);
        (await client.PostAsync($"/sessions/{sessionId}/end", content: null)).EnsureSuccessStatusCode();
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
