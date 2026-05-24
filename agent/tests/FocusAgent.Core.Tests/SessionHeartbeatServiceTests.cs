using FocusAgent.Core.Dtos;
using FocusAgent.Core.Realtime;
using FocusAgent.Core.Sessions;
using FocusAgent.Core.Settings;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace FocusAgent.Core.Tests;

public class SessionHeartbeatServiceTests
{
    [Fact]
    public async Task Pumps_Heartbeat_at_interval_while_session_is_joined()
    {
        var hub = new RecordingHub();
        var ui = new ConfirmingUi();
        var coordinator = NewCoordinator(hub, ui);
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var heartbeat = new SessionHeartbeatService(
            coordinator,
            hub,
            Options.Create(new SessionSettings { HeartbeatIntervalSeconds = 10 }),
            clock);

        var sessionId = Guid.NewGuid();
        await coordinator.HandleSessionStartedAsync(Payload(sessionId));

        await AdvanceAndDrainAsync(clock, TimeSpan.FromSeconds(10));
        await AdvanceAndDrainAsync(clock, TimeSpan.FromSeconds(10));

        Assert.True(hub.HeartbeatCalls.Count >= 2,
            $"expected >= 2 heartbeat calls after 20s, got {hub.HeartbeatCalls.Count}");
        Assert.All(hub.HeartbeatCalls, id => Assert.Equal(sessionId, id));
    }

    [Fact]
    public async Task No_pings_before_a_session_is_joined()
    {
        var hub = new RecordingHub();
        var ui = new ConfirmingUi();
        var coordinator = NewCoordinator(hub, ui);
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var heartbeat = new SessionHeartbeatService(
            coordinator,
            hub,
            Options.Create(new SessionSettings { HeartbeatIntervalSeconds = 10 }),
            clock);

        await AdvanceAndDrainAsync(clock, TimeSpan.FromMinutes(1));

        Assert.Empty(hub.HeartbeatCalls);
    }

    [Fact]
    public async Task Stops_pinging_after_SessionEnded()
    {
        var hub = new RecordingHub();
        var ui = new ConfirmingUi();
        var coordinator = NewCoordinator(hub, ui);
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var heartbeat = new SessionHeartbeatService(
            coordinator,
            hub,
            Options.Create(new SessionSettings { HeartbeatIntervalSeconds = 10 }),
            clock);

        var sessionId = Guid.NewGuid();
        await coordinator.HandleSessionStartedAsync(Payload(sessionId));

        await AdvanceAndDrainAsync(clock, TimeSpan.FromSeconds(10));
        var beforeEnd = hub.HeartbeatCalls.Count;
        Assert.True(beforeEnd >= 1);

        coordinator.HandleSessionEnded(sessionId);
        await Task.Delay(20);

        await AdvanceAndDrainAsync(clock, TimeSpan.FromSeconds(30));

        Assert.Equal(beforeEnd, hub.HeartbeatCalls.Count);
        Assert.Null(heartbeat.ActiveSessionId);
    }

    [Fact]
    public async Task Decline_path_never_starts_the_heartbeat()
    {
        var hub = new RecordingHub();
        var ui = new ConfirmingUi { Decision = JoinDecision.Declined };
        var coordinator = NewCoordinator(hub, ui);
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var heartbeat = new SessionHeartbeatService(
            coordinator,
            hub,
            Options.Create(new SessionSettings { HeartbeatIntervalSeconds = 10 }),
            clock);

        await coordinator.HandleSessionStartedAsync(Payload(Guid.NewGuid()));
        await AdvanceAndDrainAsync(clock, TimeSpan.FromMinutes(1));

        Assert.Empty(hub.HeartbeatCalls);
        Assert.Null(heartbeat.ActiveSessionId);
    }

    [Fact]
    public async Task Hub_exception_does_not_break_the_pump()
    {
        var hub = new RecordingHub { ThrowOnHeartbeat = true };
        var ui = new ConfirmingUi();
        var coordinator = NewCoordinator(hub, ui);
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var heartbeat = new SessionHeartbeatService(
            coordinator,
            hub,
            Options.Create(new SessionSettings { HeartbeatIntervalSeconds = 10 }),
            clock);

        await coordinator.HandleSessionStartedAsync(Payload(Guid.NewGuid()));

        await AdvanceAndDrainAsync(clock, TimeSpan.FromSeconds(10));
        await AdvanceAndDrainAsync(clock, TimeSpan.FromSeconds(10));
        await AdvanceAndDrainAsync(clock, TimeSpan.FromSeconds(10));

        Assert.True(hub.HeartbeatCalls.Count >= 3,
            $"expected pump to keep ticking past exceptions, got {hub.HeartbeatCalls.Count}");
    }

    private static SessionCoordinator NewCoordinator(RecordingHub hub, ConfirmingUi ui)
    {
        var settings = Options.Create(new RealtimeSettings { JoinConfirmationDuration = TimeSpan.FromSeconds(5) });
        return new SessionCoordinator(hub, ui, settings, new FakeTimeProvider(DateTimeOffset.UnixEpoch));
    }

    private static SessionStartedPayload Payload(Guid sessionId) => new(
        SessionId: sessionId,
        ClassId: Guid.NewGuid(),
        Mode: "Strict",
        StartedAt: DateTimeOffset.UnixEpoch,
        JoinCode: "123456");

    private static async Task AdvanceAndDrainAsync(FakeTimeProvider clock, TimeSpan by)
    {
        clock.Advance(by);
        // Give the heartbeat pump a slice to observe the advance and invoke
        // the hub before the assertion reads the call list.
        await Task.Delay(30);
    }

    private sealed class RecordingHub : ISessionHubConnection
    {
        public AgentConnectionState State => AgentConnectionState.Connected;
#pragma warning disable CS0067
        public event EventHandler<AgentConnectionState>? StateChanged;
        public event EventHandler<SessionStartedPayload>? SessionStarted;
        public event EventHandler<Guid>? SessionEnded;
#pragma warning restore CS0067

        public List<Guid> HeartbeatCalls { get; } = new();
        public bool ThrowOnHeartbeat { get; set; }

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task JoinSessionAsync(Guid sessionId, string? joinCode, CancellationToken ct = default) => Task.CompletedTask;
        public Task LeaveSessionAsync(Guid sessionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeclineSessionAsync(Guid sessionId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReportEventAsync(Guid sessionId, string kind, string payloadJson, DateTimeOffset? occurredAt = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task HeartbeatAsync(Guid sessionId, CancellationToken ct = default)
        {
            lock (HeartbeatCalls) HeartbeatCalls.Add(sessionId);
            if (ThrowOnHeartbeat) throw new InvalidOperationException("simulated transport failure");
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ConfirmingUi : ISessionUiHost
    {
        public JoinDecision Decision { get; set; } = JoinDecision.Confirmed;
        public Task<JoinDecision> ShowJoinConfirmationAsync(JoinConfirmation confirmation, CancellationToken ct = default)
            => Task.FromResult(Decision);
        public void DismissJoinConfirmation() { }
    }
}
