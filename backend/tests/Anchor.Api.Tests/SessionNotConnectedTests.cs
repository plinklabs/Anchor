using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
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
/// A class member whose laptop never joins a running session reaches the
/// teacher's roster and feed as not connected (#493). Every running agent gets
/// <c>SessionStarted</c> and joins after its join toast, so a student still out
/// past the join grace has no working Anchor, or isn't there; they used to sit
/// grey as "not joined" for the whole lesson while nothing was enforced for
/// them. Driven through real hub connections, as the students' agents and the
/// teacher's live page make them, on a fake clock the whole backend reads,
/// with the heartbeat monitor scanning what the session start left it.
/// </summary>
public sealed class SessionNotConnectedTests : IClassFixture<SessionNotConnectedTests.NotConnectedTestFactory>
{
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(5);

    private readonly NotConnectedTestFactory _factory;

    public SessionNotConnectedTests(NotConnectedTestFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Own factory, so the hub, the session start, the roster and the monitor
    /// all read one clock the tests move.
    /// </summary>
    public sealed class NotConnectedTestFactory : AnchorApiFactory
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
    public async Task A_class_member_who_never_joins_reaches_the_teacher_as_not_connected_once_until_they_join()
    {
        var (sessionId, startedAt, teacher, students) = await StartSessionAsync(studentCount: 3);
        var (ada, bo, cy) = (students[0], students[1], students[2]);

        await using var livePage = Connect(teacher, "Teacher");
        var feed = new RosterFeed(livePage);
        await livePage.StartAndAwaitOnConnectedAsync();
        await JoinAsync(livePage, sessionId);

        // Ada's agent joins after its 5-second join toast; Cy declines on it.
        // Bo's laptop has no Anchor running.
        await using var adaAgent = Connect(ada, "Student");
        await using var cyAgent = Connect(cy, "Student");
        await adaAgent.StartAndAwaitOnConnectedAsync();
        await cyAgent.StartAndAwaitOnConnectedAsync();
        _factory.Clock.Advance(TimeSpan.FromSeconds(5));
        await JoinAsync(adaAgent, sessionId);
        await cyAgent.InvokeAsync(
            nameof(SessionHub.DeclineSession), new DeclineSessionRequest(sessionId, Reason: "user_cancelled"));

        // 25s in: Bo's agent may still be starting.
        await PassAsync(TimeSpan.FromSeconds(20), adaAgent, sessionId);
        Assert.Empty(PushedStates(sessionId, bo.Id));
        Assert.Equal(nameof(ParticipantLiveState.NeverJoined), (await RosterRowAsync(teacher, sessionId, bo.Id)).State);

        // 35s in: past the 30s join grace, and still out.
        await PassAsync(TimeSpan.FromSeconds(10), adaAgent, sessionId);
        await feed.WaitForAsync(bo.Id, "ParticipantStateChanged:NotConnected", Arrival);
        Assert.Equal(nameof(ParticipantLiveState.NotConnected), (await RosterRowAsync(teacher, sessionId, bo.Id)).State);
        // The others answered: no marker for them.
        Assert.Equal(nameof(ParticipantLiveState.Joined), (await RosterRowAsync(teacher, sessionId, ada.Id)).State);
        Assert.Equal(nameof(ParticipantLiveState.Declined), (await RosterRowAsync(teacher, sessionId, cy.Id)).State);
        Assert.DoesNotContain(nameof(ParticipantLiveState.NotConnected), PushedStates(sessionId, ada.Id));
        Assert.DoesNotContain(nameof(ParticipantLiveState.NotConnected), PushedStates(sessionId, cy.Id));

        // Reported once, not once per scan, and kept with the session.
        await PassAsync(TimeSpan.FromMinutes(1), adaAgent, sessionId);
        Assert.Equal(new[] { nameof(ParticipantLiveState.NotConnected) }, PushedStates(sessionId, bo.Id));
        Assert.Equal(new[] { EventKind.NotConnected }, await EventKindsAsync(sessionId, bo.Id));
        Assert.DoesNotContain(EventKind.NotConnected, await EventKindsAsync(sessionId, ada.Id));
        Assert.DoesNotContain(EventKind.NotConnected, await EventKindsAsync(sessionId, cy.Id));

        // A live page that opens again rebuilds its feed from what the backend
        // stored (#492): the report is there, as of the session's start.
        var stored = Assert.Single(await StoredEventsAsync(teacher, sessionId, nameof(EventKind.NotConnected)));
        Assert.Equal(bo.Id, stored.UserId);
        using (var payload = JsonDocument.Parse(stored.PayloadJson))
            Assert.Equal(startedAt, payload.RootElement.GetProperty("expectedSince").GetDateTimeOffset());

        // Bo's agent starts after all, and joins: the marker clears.
        await using var boAgent = Connect(bo, "Student");
        await boAgent.StartAndAwaitOnConnectedAsync();
        await JoinAsync(boAgent, sessionId);
        await feed.WaitForAsync(bo.Id, "ParticipantStateChanged:Joined", Arrival);
        Assert.Equal(nameof(ParticipantLiveState.Joined), (await RosterRowAsync(teacher, sessionId, bo.Id)).State);
        Assert.Equal(
            new[] { "ParticipantStateChanged:NotConnected", "ParticipantStateChanged:Joined" },
            feed.About(bo.Id));

        await EndSessionAsync(teacher, sessionId);
    }

    [Fact]
    public async Task Someone_taking_part_by_code_is_not_waited_on_and_the_marker_holds_after_a_backend_restart()
    {
        var (sessionId, _, teacher, students) = await StartSessionAsync(studentCount: 1);
        var ada = students[0];

        // A guest outside the class takes part by code: a participant row, no
        // join until their agent's toast is answered, which it never is here.
        var guest = await TestSeed.AddUserAsync(_factory, UserRole.Student, "Guest " + Guid.NewGuid().ToString("N")[..6]);
        using (var client = _factory.CreateClient())
        {
            TestAuth.SetStudent(client, guest);
            var joinCode = await JoinCodeAsync(teacher, sessionId);
            (await client.PostAsJsonAsync("/sessions/join-by-code", new JoinByCodeRequest(joinCode)))
                .EnsureSuccessStatusCode();
        }

        await using var livePage = Connect(teacher, "Teacher");
        var feed = new RosterFeed(livePage);
        await livePage.StartAndAwaitOnConnectedAsync();
        await JoinAsync(livePage, sessionId);

        _factory.Clock.Advance(TimeSpan.FromSeconds(35));
        await ScanAsync();
        await feed.WaitForAsync(ada.Id, "ParticipantStateChanged:NotConnected", Arrival);
        Assert.Equal(nameof(ParticipantLiveState.NotConnected), (await RosterRowAsync(teacher, sessionId, ada.Id)).State);
        // Only class members are expected from the start (#410).
        Assert.Equal(nameof(ParticipantLiveState.NeverJoined), (await RosterRowAsync(teacher, sessionId, guest.Id)).State);
        Assert.Empty(PushedStates(sessionId, guest.Id));
        Assert.Empty(await EventKindsAsync(sessionId, guest.Id));

        // A backend restart empties what the monitor keeps in memory. The
        // roster reads the marker from the session's start, so a live page
        // that reloads it after reconnecting still shows it.
        _factory.Services.GetRequiredService<HeartbeatTracker>().ClearSession(sessionId);
        Assert.Equal(nameof(ParticipantLiveState.NotConnected), (await RosterRowAsync(teacher, sessionId, ada.Id)).State);

        // Once the session is over nobody is expected any more.
        await EndSessionAsync(teacher, sessionId);
        Assert.Equal(nameof(ParticipantLiveState.NeverJoined), (await RosterRowAsync(teacher, sessionId, ada.Id)).State);
    }

    [Fact]
    public async Task A_session_that_ends_within_the_grace_reports_no_one()
    {
        var (sessionId, _, teacher, students) = await StartSessionAsync(studentCount: 2);

        _factory.Clock.Advance(TimeSpan.FromSeconds(10));
        await EndSessionAsync(teacher, sessionId);
        _factory.Clock.Advance(TimeSpan.FromSeconds(30));
        await ScanAsync();

        foreach (var student in students)
        {
            Assert.Empty(PushedStates(sessionId, student.Id));
            Assert.Empty(await EventKindsAsync(sessionId, student.Id));
        }
        Assert.DoesNotContain(
            _factory.Services.GetRequiredService<HeartbeatTracker>().AwaitedJoins(),
            j => j.SessionId == sessionId);
    }

    /// <summary>
    /// Moves the clock on in 5-second steps, the monitor's scan interval, and
    /// scans after each, with the agent pinging every 10 seconds from the
    /// start of the call.
    /// </summary>
    private async Task PassAsync(TimeSpan span, HubConnection agent, Guid sessionId)
    {
        var step = TimeSpan.FromSeconds(5);
        for (var passed = step; passed <= span; passed += step)
        {
            _factory.Clock.Advance(step);
            if (passed.TotalSeconds % 10 == 0)
                await agent.InvokeAsync(nameof(SessionHub.Heartbeat), sessionId);
            await ScanAsync();
        }
    }

    /// <summary>One heartbeat-monitor pass over what the hub and the session start left it.</summary>
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
            .OrderBy(c => c.At)
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

    private async Task<string> JoinCodeAsync(User teacher, Guid sessionId)
    {
        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, teacher);
        var detail = await client.GetFromJsonAsync<SessionDetailResponse>($"/sessions/{sessionId}");
        return detail!.JoinCode;
    }

    /// <summary>The session's stored events of <paramref name="kind"/>, as the live page loads its feed's history.</summary>
    private async Task<IReadOnlyList<SessionRecentEvent>> StoredEventsAsync(User teacher, Guid sessionId, string kind)
    {
        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, teacher);
        return (await client.GetFromJsonAsync<List<SessionRecentEvent>>($"/sessions/{sessionId}/events?kind={kind}"))!;
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

    private async Task<(Guid SessionId, DateTimeOffset StartedAt, User Teacher, IReadOnlyList<User> Students)> StartSessionAsync(
        int studentCount)
    {
        var scenario = await TestSeed.SeedClassWithTeacherAndStudentsAsync(_factory, studentCount);
        using var client = _factory.CreateClient();
        TestAuth.SetTeacher(client, scenario.Teacher);
        var response = await client.PostAsJsonAsync("/sessions", new StartSessionRequest(scenario.Class.Id, null));
        response.EnsureSuccessStatusCode();
        var started = await response.Content.ReadFromJsonAsync<StartSessionResponse>();
        return (started!.Id, started.StartedAt, scenario.Teacher, scenario.Students);
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
    /// signal), where a signal is <c>ParticipantStateChanged:{state}</c>.
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
