using System.Diagnostics;
using FocusAgent.Core.Dtos;
using FocusAgent.Core.Realtime;
using FocusAgent.Core.Sessions;
using FocusAgent.Core.Settings;
using FocusAgent.Core.Updates;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FakeUpdateManager = FocusAgent.Core.Tests.AgentUpdateServiceTests.FakeUpdateManager;

namespace FocusAgent.Core.Tests;

/// <summary>
/// Unit coverage for #456's restart policy: the agent restarts into a downloaded
/// update once no session is going on, and never during one or while a join is
/// pending. Drives the real <see cref="IdleUpdateRestarter"/> over a real
/// <see cref="AgentUpdateService"/> (fake manager) and a real
/// <see cref="SessionCoordinator"/> (fake hub and UI); only the hand-over to the
/// updater is a fake. The hand-over itself, and the agent exiting on its own, are
/// proven end-to-end by UpdateApplyTests.
/// </summary>
public class IdleUpdateRestarterTests
{
    private const string Version = "1.2.3";

    // Generous: a passing test never waits this long.
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Restarts_into_a_downloaded_update_when_no_session_is_going_on()
    {
        await using var rig = await Rig.WithReadyUpdateAsync();

        Assert.True(rig.Restarter.TryRestart("test"));

        Assert.Equal(new[] { Version }, rig.Agent.Restarts);
        Assert.True(rig.Sessions.IsClosingForRestart);
    }

    [Fact]
    public async Task Started_with_an_update_already_waiting_restarts_into_it()
    {
        await using var rig = await Rig.WithReadyUpdateAsync();

        rig.Restarter.Start();

        await rig.Agent.WaitForRestartAsync();
        Assert.Equal(new[] { Version }, rig.Agent.Restarts);
    }

    [Fact]
    public async Task Restarts_when_an_update_is_downloaded_after_it_started()
    {
        await using var rig = new Rig();
        rig.Restarter.Start();

        rig.Updates.Start();

        await rig.Agent.WaitForRestartAsync();
        Assert.Equal(new[] { Version }, rig.Agent.Restarts);
    }

    [Fact]
    public async Task Does_nothing_without_a_downloaded_update()
    {
        await using var rig = new Rig(update: AgentUpdateCheckResult.None);
        rig.Updates.Start();
        await rig.Updates.InFlight.WaitAsync(WaitTimeout);

        Assert.False(rig.Restarter.TryRestart("test"));

        Assert.Empty(rig.Agent.Restarts);
        Assert.False(rig.Sessions.IsClosingForRestart);
    }

    [Fact]
    public async Task Waits_while_in_a_session_and_restarts_once_it_ends()
    {
        await using var rig = await Rig.WithReadyUpdateAsync();
        var session = Payload();
        await rig.Sessions.RejoinAsync(session);
        rig.Restarter.Start();

        Assert.False(rig.Restarter.TryRestart("test"));
        Assert.Empty(rig.Agent.Restarts);
        Assert.False(rig.Sessions.IsClosingForRestart);

        rig.Sessions.HandleSessionEnded(session.SessionId);

        await rig.Agent.WaitForRestartAsync();
        Assert.Equal(new[] { Version }, rig.Agent.Restarts);
    }

    [Fact]
    public async Task Waits_while_the_student_is_asked_to_join_and_restarts_after_a_decline()
    {
        await using var rig = await Rig.WithReadyUpdateAsync();
        var asking = rig.Sessions.HandleSessionStartedAsync(Payload());
        await rig.Ui.WaitForShownAsync();

        Assert.False(rig.Restarter.TryRestart("test"));
        Assert.Empty(rig.Agent.Restarts);

        // A decline ends no joined session, so no SessionLeft: the retry finds it.
        rig.Ui.Resolve(JoinDecision.Declined);
        await asking;
        rig.Clock.Advance(IdleUpdateRestarter.RetryInterval);

        Assert.Equal(new[] { Version }, rig.Agent.Restarts);
    }

    [Fact]
    public async Task Waits_while_a_rejoin_is_on_its_way()
    {
        var gate = new TaskCompletionSource();
        await using var rig = await Rig.WithReadyUpdateAsync();
        rig.Hub.JoinGate = gate.Task;

        var rejoin = rig.Sessions.RejoinAsync(Payload());
        Assert.False(rig.Restarter.TryRestart("test"));

        gate.SetResult();
        await rejoin;
        Assert.False(rig.Restarter.TryRestart("test"));
        Assert.Empty(rig.Agent.Restarts);
    }

    [Fact]
    public async Task Waits_while_the_join_by_code_dialog_is_open_and_retries_a_minute_later()
    {
        var busy = true;
        await using var rig = await Rig.WithReadyUpdateAsync(isBusy: () => Volatile.Read(ref busy));

        Assert.False(rig.Restarter.TryRestart("test"));
        Assert.False(rig.Sessions.IsClosingForRestart);

        Volatile.Write(ref busy, false);
        rig.Clock.Advance(IdleUpdateRestarter.RetryInterval);

        Assert.Equal(new[] { Version }, rig.Agent.Restarts);
    }

    [Fact]
    public async Task A_failed_hand_over_keeps_the_agent_taking_sessions_and_tries_again_only_after_the_next_check()
    {
        await using var rig = await Rig.WithReadyUpdateAsync();
        rig.Agent.HandOver = false;
        rig.Restarter.Start();
        await rig.Agent.WaitForRestartAsync();

        // Back to normal: sessions are taken, and the version is held back.
        Assert.False(rig.Sessions.IsClosingForRestart);
        Assert.Null(rig.Updates.ReadyToApplyVersion);
        Assert.False(rig.Restarter.TryRestart("test"));
        rig.Clock.Advance(IdleUpdateRestarter.RetryInterval * 5);
        Assert.Single(rig.Agent.Restarts);

        // The next check finds the update again and the agent tries once more.
        rig.Agent.HandOver = true;
        rig.Clock.Advance(Rig.Interval);
        await rig.Agent.WaitForRestartsAsync(2);
        Assert.Equal(new[] { Version, Version }, rig.Agent.Restarts);
    }

    [Fact]
    public async Task A_throwing_hand_over_counts_as_failed()
    {
        await using var rig = await Rig.WithReadyUpdateAsync();
        rig.Agent.Throw = true;

        Assert.False(rig.Restarter.TryRestart("test"));

        Assert.False(rig.Sessions.IsClosingForRestart);
        Assert.Null(rig.Updates.ReadyToApplyVersion);
    }

    [Fact]
    public async Task Hands_over_only_once()
    {
        await using var rig = await Rig.WithReadyUpdateAsync();

        Assert.True(rig.Restarter.TryRestart("test"));
        Assert.False(rig.Restarter.TryRestart("test"));

        Assert.Single(rig.Agent.Restarts);
    }

    [Fact]
    public async Task Once_stopped_by_a_fatal_path_it_never_restarts()
    {
        await using var rig = new Rig();
        rig.Restarter.Start();
        rig.Restarter.Stop();

        rig.Updates.Start();
        await rig.Updates.InFlight.WaitAsync(WaitTimeout);

        Assert.False(rig.Restarter.TryRestart("test"));
        Assert.Empty(rig.Agent.Restarts);
        Assert.False(rig.Sessions.IsClosingForRestart);
    }

    private static SessionStartedPayload Payload() =>
        new(SessionId: Guid.NewGuid(),
            ClassId: Guid.NewGuid(),
            StartedAt: DateTimeOffset.UnixEpoch,
            JoinCode: "123456",
            Apps: Array.Empty<AllowedAppDto>(),
            Domains: Array.Empty<AllowedDomainDto>());

    /// <summary>The restarter over a real update service and coordinator.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UnixEpoch);
        public FakeHub Hub { get; } = new();
        public FakeUi Ui { get; } = new();
        public FakeAgent Agent { get; } = new();
        public AgentUpdateService Updates { get; }
        public SessionCoordinator Sessions { get; }
        public IdleUpdateRestarter Restarter { get; }

        public Rig(AgentUpdateCheckResult? update = null, Func<bool>? isBusy = null)
        {
            var manager = new FakeUpdateManager
            {
                IsInstalled = true,
                NextResult = update ?? new AgentUpdateCheckResult(true, Version, new object()),
            };
            Updates = new AgentUpdateService(
                manager, Options.Create(new UpdateSettings { CheckInterval = Interval }), Clock);
            Sessions = new SessionCoordinator(
                Hub, Ui, Options.Create(new RealtimeSettings()), Clock);
            Restarter = new IdleUpdateRestarter(Updates, Sessions, Agent, isBusy, Clock);
        }

        /// <summary>A rig whose startup pass has downloaded <see cref="Version"/>, not yet started.</summary>
        public static async Task<Rig> WithReadyUpdateAsync(Func<bool>? isBusy = null)
        {
            var rig = new Rig(isBusy: isBusy);
            rig.Updates.Start();
            await rig.Updates.InFlight.WaitAsync(WaitTimeout);
            Assert.Equal(Version, rig.Updates.ReadyToApplyVersion);
            return rig;
        }

        public async ValueTask DisposeAsync()
        {
            Restarter.Dispose();
            await Updates.DisposeAsync();
            await Sessions.DisposeAsync();
        }
    }

    /// <summary>The app's side of the restart: records each hand-over.</summary>
    private sealed class FakeAgent : IAgentRestarter
    {
        private readonly List<string> _restarts = new();

        public bool HandOver { get; set; } = true;
        public bool Throw { get; set; }

        public IReadOnlyList<string> Restarts
        {
            get { lock (_restarts) return _restarts.ToList(); }
        }

        public bool TryRestartIntoUpdate(string version)
        {
            lock (_restarts) _restarts.Add(version);
            if (Throw) throw new InvalidOperationException("updater missing (test)");
            return HandOver;
        }

        public Task WaitForRestartAsync() => WaitForRestartsAsync(1);

        public async Task WaitForRestartsAsync(int count)
        {
            var elapsed = Stopwatch.StartNew();
            while (Restarts.Count < count)
            {
                if (elapsed.Elapsed > WaitTimeout)
                    Assert.Fail($"timed out waiting for restart #{count} (got {Restarts.Count})");
                await Task.Delay(1);
            }
            // A moment more, so a hand-over that must not happen has its chance to.
            await Task.Delay(50);
        }
    }

    private sealed class FakeHub : ISessionHubConnection
    {
        public AgentConnectionState State => AgentConnectionState.Connected;
#pragma warning disable CS0067 // Interface events not used by these tests.
        public event EventHandler<AgentConnectionState>? StateChanged;
        public event EventHandler<SessionStartedPayload>? SessionStarted;
        public event EventHandler<Guid>? SessionEnded;
        public event EventHandler<SessionBundlesUpdatedPayload>? SessionBundlesUpdated;
#pragma warning restore CS0067

        public Task? JoinGate { get; set; }

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task JoinSessionAsync(Guid sessionId, string? joinCode, CancellationToken ct = default) =>
            JoinGate ?? Task.CompletedTask;
        public Task LeaveSessionAsync(Guid sessionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeclineSessionAsync(Guid sessionId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReportEventAsync(Guid sessionId, string kind, string payloadJson, DateTimeOffset? occurredAt = null, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<bool> HeartbeatAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> IsInSessionAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<SessionStartedPayload?> GetStartedSessionAsync(CancellationToken ct = default) =>
            Task.FromResult<SessionStartedPayload?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeUi : ISessionUiHost
    {
        private readonly TaskCompletionSource _shown = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<JoinDecision> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<JoinDecision> ShowJoinConfirmationAsync(JoinConfirmation confirmation, CancellationToken ct = default)
        {
            _shown.TrySetResult();
            return _decision.Task;
        }

        public void DismissJoinConfirmation() => _decision.TrySetResult(JoinDecision.Aborted);

        public Task WaitForShownAsync() => _shown.Task.WaitAsync(WaitTimeout);

        public void Resolve(JoinDecision decision) => _decision.TrySetResult(decision);
    }
}
