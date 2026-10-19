using FocusAgent.Core.Dtos;
using FocusAgent.Core.Focus;
using FocusAgent.Core.Realtime;
using FocusAgent.Core.Sessions;
using FocusAgent.Core.Settings;
using FocusAgent.Core.Tamper;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace FocusAgent.Core.Tests;

/// <summary>
/// The Edge pause's state machine (#427): when a switched-off extension pauses
/// Edge, and every way the extension legitimately goes away (Edge closing or
/// restarting, the extension updating or reloading) that must not.
/// </summary>
public class ExtensionGuardTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Repair = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Switching_the_extension_off_with_edge_open_pauses_edge_after_the_grace_period()
    {
        var h = new Harness();
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        await h.JoinAsync();
        Assert.Equal(ExtensionGuardState.Present, h.Guard.State);

        h.Transport.RaiseDisconnected();

        // Not yet: the grace period covers a reload or an Edge restart.
        Assert.Equal(ExtensionGuardState.Waiting, h.Guard.State);
        h.Clock.Advance(Grace - TimeSpan.FromSeconds(1));
        Assert.False(h.Guard.IsBrowserPaused);
        Assert.Equal(0, h.PausedEvents);

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(h.Guard.IsBrowserPaused);
        Assert.Equal(1, h.PausedEvents);
    }

    [Fact]
    public async Task The_pause_lifts_as_soon_as_the_extension_reconnects()
    {
        var h = new Harness();
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        await h.JoinAsync();
        h.Transport.RaiseDisconnected();
        h.Clock.Advance(Grace);
        Assert.True(h.Guard.IsBrowserPaused);

        h.Transport.RaiseConnected();

        Assert.Equal(ExtensionGuardState.Present, h.Guard.State);
        Assert.False(h.Guard.IsBrowserPaused);
    }

    [Fact]
    public async Task An_extension_reload_or_update_that_reconnects_within_the_grace_period_never_pauses_edge()
    {
        var h = new Harness();
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        await h.JoinAsync();

        h.Transport.RaiseDisconnected();
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.Transport.RaiseConnected();
        h.Clock.Advance(Grace * 3);

        Assert.Equal(ExtensionGuardState.Present, h.Guard.State);
        Assert.Equal(0, h.PausedEvents);
    }

    [Fact]
    public async Task Closing_edge_pauses_nothing_and_a_relaunch_that_reconnects_in_time_is_never_paused()
    {
        var h = new Harness();
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        await h.JoinAsync();

        // The student closes Edge: the link drops with no Edge window left.
        h.NoEdgeOnScreen();
        h.Transport.RaiseDisconnected();
        h.Clock.Advance(Grace);
        Assert.Equal(ExtensionGuardState.Dormant, h.Guard.State);
        Assert.False(h.Guard.IsBrowserPaused);

        // Minutes later they open Edge again. Its window comes forward before the
        // extension has loaded; that must start a fresh grace period, not pause.
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        h.EdgeOnScreen();
        h.Guard.NoteBrowserForeground();
        Assert.Equal(ExtensionGuardState.Waiting, h.Guard.State);
        Assert.False(h.Guard.IsBrowserPaused);

        h.Clock.Advance(TimeSpan.FromSeconds(3));
        h.Transport.RaiseConnected();
        h.Clock.Advance(Grace * 2);

        Assert.Equal(ExtensionGuardState.Present, h.Guard.State);
        Assert.Equal(0, h.PausedEvents);
    }

    [Fact]
    public async Task Edge_relaunched_with_the_extension_still_off_is_paused_after_a_fresh_grace_period()
    {
        var h = new Harness();
        h.NoEdgeOnScreen();
        h.Transport.RaiseConnected();
        await h.JoinAsync();
        h.Transport.RaiseDisconnected();
        h.Clock.Advance(Grace);
        Assert.Equal(ExtensionGuardState.Dormant, h.Guard.State);

        h.EdgeOnScreen();
        h.Guard.NoteBrowserForeground();
        h.Clock.Advance(Grace);

        Assert.True(h.Guard.IsBrowserPaused);
        Assert.Equal(1, h.PausedEvents);
    }

    [Fact]
    public async Task An_extension_switched_off_before_the_session_pauses_edge_after_the_grace_period()
    {
        var h = new Harness();
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        h.Transport.RaiseDisconnected();   // switched off before the lesson

        await h.JoinAsync();
        Assert.Equal(ExtensionGuardState.Waiting, h.Guard.State);
        h.Clock.Advance(Grace);

        Assert.True(h.Guard.IsBrowserPaused);
    }

    [Fact]
    public async Task A_device_where_the_extension_never_connected_never_pauses_edge()
    {
        // Not installed yet, or the host isn't registered: the agent can't tell
        // that from "switched off", so it leaves Edge alone.
        var h = new Harness();
        h.EdgeOnScreen();

        await h.JoinAsync();
        h.Clock.Advance(Grace * 3);
        h.Guard.NoteBrowserForeground();
        h.Clock.Advance(Grace * 3);

        Assert.Equal(ExtensionGuardState.Off, h.Guard.State);
        Assert.Equal(0, h.PausedEvents);
    }

    [Fact]
    public void A_drop_outside_a_session_pauses_nothing()
    {
        var h = new Harness();
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();

        h.Transport.RaiseDisconnected();
        h.Clock.Advance(Grace * 3);

        Assert.Equal(ExtensionGuardState.Off, h.Guard.State);
        Assert.Equal(0, h.PausedEvents);
    }

    [Fact]
    public async Task Leaving_the_session_lifts_the_pause_and_cancels_a_pending_grace_period()
    {
        var h = new Harness();
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        var sessionId = await h.JoinAsync();
        h.Transport.RaiseDisconnected();
        h.Clock.Advance(Grace);
        Assert.True(h.Guard.IsBrowserPaused);

        h.Hub.RaiseSessionEnded(sessionId);
        Assert.Equal(ExtensionGuardState.Off, h.Guard.State);

        // A new session with the extension still off starts a fresh grace period…
        var next = await h.JoinAsync();
        Assert.Equal(ExtensionGuardState.Waiting, h.Guard.State);
        // …which leaving cancels: no pause fires afterwards.
        h.Hub.RaiseSessionEnded(next);
        h.Clock.Advance(Grace * 2);
        Assert.Equal(ExtensionGuardState.Off, h.Guard.State);
        Assert.Equal(1, h.PausedEvents);
    }

    [Fact]
    public async Task Turning_the_extension_back_on_opens_edge_for_the_repair_window_then_pauses_it_again()
    {
        var h = new Harness();
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        await h.JoinAsync();
        h.Transport.RaiseDisconnected();
        h.Clock.Advance(Grace);
        Assert.True(h.Guard.IsBrowserPaused);

        Assert.True(h.Guard.BeginRepair());
        Assert.Equal(ExtensionGuardState.Repairing, h.Guard.State);
        Assert.False(h.Guard.IsBrowserPaused);

        // The student didn't switch it on in time: Edge is paused again.
        h.Clock.Advance(Repair);
        Assert.True(h.Guard.IsBrowserPaused);
        Assert.Equal(2, h.PausedEvents);
    }

    [Fact]
    public async Task Switching_the_extension_on_during_the_repair_window_lifts_the_pause_for_good()
    {
        var h = new Harness();
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        await h.JoinAsync();
        h.Transport.RaiseDisconnected();
        h.Clock.Advance(Grace);
        h.Guard.BeginRepair();

        h.Clock.Advance(TimeSpan.FromSeconds(20));
        h.Transport.RaiseConnected();
        h.Clock.Advance(Repair * 2);

        Assert.Equal(ExtensionGuardState.Present, h.Guard.State);
        Assert.Equal(1, h.PausedEvents);
    }

    [Fact]
    public async Task Repair_is_refused_unless_edge_is_paused()
    {
        var h = new Harness();
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        await h.JoinAsync();

        Assert.False(h.Guard.BeginRepair());
        Assert.Equal(ExtensionGuardState.Present, h.Guard.State);
    }

    [Fact]
    public void Recognises_the_browser_by_process_name_and_its_allowlist_rules()
    {
        var h = new Harness();

        Assert.True(h.Guard.IsBrowser(new AppInfo("msedge", null, null)));
        Assert.True(h.Guard.IsBrowser(new AppInfo("MSEDGE.exe", null, null)));
        Assert.False(h.Guard.IsBrowser(new AppInfo("msedgewebview2", null, null)));
        Assert.False(h.Guard.IsBrowser(new AppInfo("winword", null, null)));

        Assert.True(h.Guard.IsBrowserRule(new AllowedAppRule { MatchKind = AllowedAppMatchKind.ProcessName, Value = "msedge.exe" }));
        Assert.True(h.Guard.IsBrowserRule(new AllowedAppRule
        {
            MatchKind = AllowedAppMatchKind.ExecutablePath,
            Value = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        }));
        Assert.False(h.Guard.IsBrowserRule(new AllowedAppRule { MatchKind = AllowedAppMatchKind.ProcessName, Value = "winword" }));
        Assert.False(h.Guard.IsBrowserRule(new AllowedAppRule { MatchKind = AllowedAppMatchKind.Publisher, Value = "Microsoft Corporation" }));
        Assert.Equal("msedge", h.Guard.BrowserRule.Value);
    }

    private sealed class Harness
    {
        public FakeHub Hub { get; } = new();
        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UnixEpoch);
        public FakeWitnessTransport Transport { get; } = new();
        public FakeWindows Windows { get; } = new();
        public SessionCoordinator Coordinator { get; }
        public ExtensionWitnessMonitor Witness { get; }
        public ExtensionGuard Guard { get; }
        public int PausedEvents { get; private set; }

        public Harness()
        {
            Coordinator = new SessionCoordinator(
                Hub,
                new ConfirmingUi(),
                Options.Create(new RealtimeSettings { JoinConfirmationDuration = TimeSpan.FromSeconds(5) }),
                Clock);
            Witness = new ExtensionWitnessMonitor(Transport, new NoopReporter(), () => Coordinator.JoinedSessionId);
            Guard = new ExtensionGuard(
                Coordinator,
                Witness,
                Windows,
                Options.Create(new SessionSettings
                {
                    ExtensionGraceSeconds = (int)Grace.TotalSeconds,
                    ExtensionRepairSeconds = (int)Repair.TotalSeconds,
                }),
                Options.Create(new WitnessSettings()),
                Clock);
            Guard.BrowserPaused += (_, _) => PausedEvents++;
        }

        public void EdgeOnScreen() =>
            Windows.Windows = new[]
            {
                new OpenWindow(0x10, new AppInfo("winword", null, null)),
                new OpenWindow(0x20, new AppInfo("msedge", null, null)),
            };

        public void NoEdgeOnScreen() =>
            Windows.Windows = new[] { new OpenWindow(0x10, new AppInfo("winword", null, null)) };

        public async Task<Guid> JoinAsync()
        {
            var payload = new SessionStartedPayload(
                SessionId: Guid.NewGuid(),
                ClassId: Guid.NewGuid(),
                StartedAt: DateTimeOffset.UnixEpoch,
                JoinCode: "123456",
                Apps: new[] { new AllowedAppDto("ProcessName", "msedge") },
                Domains: Array.Empty<AllowedDomainDto>());
            await Coordinator.HandleSessionStartedAsync(payload);
            Assert.Equal(payload.SessionId, Coordinator.JoinedSessionId);
            return payload.SessionId;
        }
    }

    private sealed class FakeWindows : IWindowEnumerator
    {
        public IReadOnlyList<OpenWindow> Windows { get; set; } = Array.Empty<OpenWindow>();
        public IReadOnlyList<OpenWindow> GetOpenWindows() => Windows;
    }

    private sealed class FakeWitnessTransport : IExtensionWitnessTransport
    {
        public event EventHandler? WitnessConnected;
        public event EventHandler? WitnessDisconnected;
        public void RaiseConnected() => WitnessConnected?.Invoke(this, EventArgs.Empty);
        public void RaiseDisconnected() => WitnessDisconnected?.Invoke(this, EventArgs.Empty);
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopReporter : ITamperReporter
    {
        public Task ReportAsync(Guid sessionId, string kind, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ConfirmingUi : ISessionUiHost
    {
        public Task<JoinDecision> ShowJoinConfirmationAsync(JoinConfirmation confirmation, CancellationToken ct = default)
            => Task.FromResult(JoinDecision.Confirmed);
        public void DismissJoinConfirmation() { }
    }

    private sealed class FakeHub : ISessionHubConnection
    {
        public AgentConnectionState State => AgentConnectionState.Connected;
#pragma warning disable CS0067
        public event EventHandler<AgentConnectionState>? StateChanged;
        public event EventHandler<SessionStartedPayload>? SessionStarted;
        public event EventHandler<SessionBundlesUpdatedPayload>? SessionBundlesUpdated;
#pragma warning restore CS0067
        public event EventHandler<Guid>? SessionEnded;

        public void RaiseSessionEnded(Guid sessionId) => SessionEnded?.Invoke(this, sessionId);

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task JoinSessionAsync(Guid sessionId, string? joinCode, CancellationToken ct = default) => Task.CompletedTask;
        public Task LeaveSessionAsync(Guid sessionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeclineSessionAsync(Guid sessionId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReportEventAsync(Guid sessionId, string kind, string payloadJson, DateTimeOffset? occurredAt = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> HeartbeatAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> IsInSessionAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<SessionStartedPayload?> GetStartedSessionAsync(CancellationToken ct = default) => Task.FromResult<SessionStartedPayload?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
