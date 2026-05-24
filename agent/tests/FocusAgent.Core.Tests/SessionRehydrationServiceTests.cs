using FocusAgent.Core.Dtos;
using FocusAgent.Core.Realtime;
using FocusAgent.Core.Sessions;
using FocusAgent.Core.Settings;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace FocusAgent.Core.Tests;

public class SessionRehydrationServiceTests
{
    [Fact]
    public async Task Rejoins_each_session_returned_by_the_client_on_first_NotifyConnectedAsync()
    {
        var hub = new RecordingHub();
        var ui = new SilentUi();
        var coordinator = NewCoordinator(hub, ui);
        var s1 = Payload();
        var s2 = Payload();
        var client = new StubClient(new[] { s1, s2 });

        var service = new SessionRehydrationService(client, coordinator);

        await service.NotifyConnectedAsync();

        Assert.Equal(1, client.CallCount);
        Assert.Equal(2, hub.JoinCalls.Count);
        Assert.Contains(hub.JoinCalls, c => c.SessionId == s1.SessionId);
        Assert.Contains(hub.JoinCalls, c => c.SessionId == s2.SessionId);
        Assert.True(service.HasRehydrated);
    }

    [Fact]
    public async Task Second_NotifyConnectedAsync_after_success_is_a_no_op()
    {
        var hub = new RecordingHub();
        var ui = new SilentUi();
        var coordinator = NewCoordinator(hub, ui);
        var client = new StubClient(new[] { Payload() });
        var service = new SessionRehydrationService(client, coordinator);

        await service.NotifyConnectedAsync();
        await service.NotifyConnectedAsync();
        await service.NotifyConnectedAsync();

        Assert.Equal(1, client.CallCount);
        Assert.Single(hub.JoinCalls);
    }

    [Fact]
    public async Task Failure_does_not_latch_the_completed_flag_so_a_later_NotifyConnectedAsync_retries()
    {
        var hub = new RecordingHub();
        var ui = new SilentUi();
        var coordinator = NewCoordinator(hub, ui);
        var client = new StubClient(new[] { Payload() }) { FailOnce = true };
        var service = new SessionRehydrationService(client, coordinator);

        await service.NotifyConnectedAsync();
        Assert.False(service.HasRehydrated);
        Assert.Equal(1, client.CallCount);
        Assert.Empty(hub.JoinCalls);

        await service.NotifyConnectedAsync();
        Assert.True(service.HasRehydrated);
        Assert.Equal(2, client.CallCount);
        Assert.Single(hub.JoinCalls);
    }

    [Fact]
    public async Task Empty_rejoinable_list_marks_rehydration_complete_without_calling_hub()
    {
        var hub = new RecordingHub();
        var ui = new SilentUi();
        var coordinator = NewCoordinator(hub, ui);
        var client = new StubClient(Array.Empty<SessionStartedPayload>());
        var service = new SessionRehydrationService(client, coordinator);

        await service.NotifyConnectedAsync();

        Assert.True(service.HasRehydrated);
        Assert.Equal(1, client.CallCount);
        Assert.Empty(hub.JoinCalls);

        // And a subsequent NotifyConnectedAsync shouldn't refetch.
        await service.NotifyConnectedAsync();
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task Concurrent_NotifyConnectedAsync_calls_only_fetch_once()
    {
        var hub = new RecordingHub();
        var ui = new SilentUi();
        var coordinator = NewCoordinator(hub, ui);
        var client = new StubClient(new[] { Payload() }) { DelayMs = 100 };
        var service = new SessionRehydrationService(client, coordinator);

        var t1 = service.NotifyConnectedAsync();
        var t2 = service.NotifyConnectedAsync();
        await Task.WhenAll(t1, t2);

        // The second call hits the gate-busy short-circuit (WaitAsync(0))
        // and returns without fetching.
        Assert.Equal(1, client.CallCount);
        Assert.Single(hub.JoinCalls);
    }

    [Fact]
    public async Task Rejoin_skips_session_already_joined_via_SessionStarted_broadcast()
    {
        // Simulates: rehydration is fetching; meanwhile the teacher broadcasts
        // SessionStarted for the same session and the user auto-confirms it
        // before rehydration calls RejoinAsync. SessionCoordinator.RejoinAsync
        // is itself idempotent, so the hub only sees one JoinSession.
        var hub = new RecordingHub();
        var ui = new SilentUi();
        var coordinator = NewCoordinator(hub, ui);
        var payload = Payload();

        // Simulate a prior "real" join (e.g. user confirmed the toast).
        await coordinator.HandleSessionStartedAsync(payload);
        Assert.Single(hub.JoinCalls);
        Assert.Equal(payload.SessionId, coordinator.JoinedSessionId);

        var client = new StubClient(new[] { payload });
        var service = new SessionRehydrationService(client, coordinator);
        await service.NotifyConnectedAsync();

        Assert.Equal(1, client.CallCount);
        Assert.Single(hub.JoinCalls);
    }

    private static SessionCoordinator NewCoordinator(RecordingHub hub, SilentUi ui)
    {
        var settings = Options.Create(new RealtimeSettings { JoinConfirmationDuration = TimeSpan.FromSeconds(5) });
        return new SessionCoordinator(hub, ui, settings, new FakeTimeProvider(DateTimeOffset.UnixEpoch));
    }

    private static SessionStartedPayload Payload(Guid? sessionId = null) => new(
        SessionId: sessionId ?? Guid.NewGuid(),
        ClassId: Guid.NewGuid(),
        Mode: "Strict",
        StartedAt: DateTimeOffset.UnixEpoch,
        JoinCode: "987654",
        Apps: Array.Empty<AllowedAppDto>(),
        Domains: Array.Empty<AllowedDomainDto>());

    private sealed class StubClient : ISessionRehydrationClient
    {
        private readonly IReadOnlyList<SessionStartedPayload> _payloads;
        public int CallCount { get; private set; }
        public bool FailOnce { get; set; }
        public int DelayMs { get; set; }

        public StubClient(IReadOnlyList<SessionStartedPayload> payloads) { _payloads = payloads; }

        public async Task<IReadOnlyList<SessionStartedPayload>> GetRejoinableSessionsAsync(CancellationToken ct = default)
        {
            CallCount++;
            if (DelayMs > 0)
                await Task.Delay(DelayMs, ct).ConfigureAwait(false);
            if (FailOnce)
            {
                FailOnce = false;
                throw new InvalidOperationException("simulated transport failure");
            }
            return _payloads;
        }
    }

    private sealed class RecordingHub : ISessionHubConnection
    {
        public AgentConnectionState State => AgentConnectionState.Connected;
#pragma warning disable CS0067
        public event EventHandler<AgentConnectionState>? StateChanged;
        public event EventHandler<SessionStartedPayload>? SessionStarted;
        public event EventHandler<Guid>? SessionEnded;
#pragma warning restore CS0067

        public List<(Guid SessionId, string? JoinCode)> JoinCalls { get; } = new();

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task JoinSessionAsync(Guid sessionId, string? joinCode, CancellationToken ct = default)
        {
            lock (JoinCalls) JoinCalls.Add((sessionId, joinCode));
            return Task.CompletedTask;
        }
        public Task LeaveSessionAsync(Guid sessionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeclineSessionAsync(Guid sessionId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReportEventAsync(Guid sessionId, string kind, string payloadJson, DateTimeOffset? occurredAt = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> HeartbeatAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SilentUi : ISessionUiHost
    {
        public Task<JoinDecision> ShowJoinConfirmationAsync(JoinConfirmation confirmation, CancellationToken ct = default)
            => Task.FromResult(JoinDecision.Confirmed);
        public void DismissJoinConfirmation() { }
    }
}
