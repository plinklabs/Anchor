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
/// restarting, the extension updating or reloading) that must not. And what the
/// teacher is told (#518): "switched off or removed" only when Edge is paused
/// for it, once per outage, and with several profiles only for a profile that is
/// still open.
/// </summary>
public class ExtensionGuardTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Repair = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Scan = TimeSpan.FromSeconds(5);

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

    // ---- #518: what the teacher is told ----

    [Fact]
    public async Task A_drop_that_reconnects_within_the_grace_period_tells_the_teacher_nothing()
    {
        // The evidence of #518: changing an option on the extension's page makes
        // Edge restart it, and it was back 0.01 to 1.05 s later, three times.
        var h = new Harness();
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        await h.JoinAsync();

        foreach (var away in new[] { 1.05, 0.01, 0.36 })
        {
            h.Transport.RaiseDisconnected();
            h.Clock.Advance(TimeSpan.FromSeconds(away));
            h.Transport.RaiseConnected();
            h.Clock.Advance(TimeSpan.FromSeconds(30));
        }

        Assert.Empty(h.Reporter.Calls);
        Assert.Equal(ExtensionGuardState.Present, h.Guard.State);
    }

    [Fact]
    public async Task A_drop_that_outlasts_the_grace_period_with_edge_open_tells_the_teacher_once()
    {
        var h = new Harness();
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        var sessionId = await h.JoinAsync();

        h.Transport.RaiseDisconnected();
        h.Clock.Advance(Grace - TimeSpan.FromSeconds(1));
        Assert.Empty(h.Reporter.Calls);   // not on the drop itself

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        var call = Assert.Single(h.Reporter.Calls);
        Assert.Equal((sessionId, TamperKinds.ExtensionDisabled), call);
        Assert.Equal("extension_disabled", call.Kind);

        // The student opens Edge to repair it and doesn't: paused again, but the
        // teacher already knows.
        Assert.True(h.Guard.BeginRepair());
        h.Clock.Advance(Repair);
        Assert.True(h.Guard.IsBrowserPaused);
        h.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Single(h.Reporter.Calls);
    }

    [Fact]
    public async Task Closing_edge_tells_the_teacher_nothing()
    {
        var h = new Harness();
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        await h.JoinAsync();

        h.NoEdgeOnScreen();
        h.Transport.RaiseDisconnected();
        h.Clock.Advance(Grace * 6);

        Assert.Equal(ExtensionGuardState.Dormant, h.Guard.State);
        Assert.Empty(h.Reporter.Calls);

        // Opened again later, with the extension loading in time: still nothing.
        h.EdgeOnScreen();
        h.Guard.NoteBrowserForeground();
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.Transport.RaiseConnected();
        h.Clock.Advance(Grace * 2);
        Assert.Empty(h.Reporter.Calls);
    }

    [Fact]
    public async Task Edge_reopened_with_the_extension_still_off_tells_the_teacher_then()
    {
        // Switched off, and Edge closed before the grace period ran out: nothing
        // yet. When Edge is back without it, it is really gone with Edge open.
        var h = new Harness();
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        await h.JoinAsync();
        h.Transport.RaiseDisconnected();
        h.NoEdgeOnScreen();
        h.Clock.Advance(Grace);
        Assert.Empty(h.Reporter.Calls);

        h.EdgeOnScreen();
        h.Guard.NoteBrowserForeground();
        h.Clock.Advance(Grace);

        Assert.True(h.Guard.IsBrowserPaused);
        Assert.Single(h.Reporter.Calls);
    }

    [Fact]
    public async Task Each_outage_is_told_once()
    {
        var h = new Harness();
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        await h.JoinAsync();

        h.Transport.RaiseDisconnected();   // switched off
        h.Clock.Advance(Grace);
        h.Transport.RaiseConnected();      // switched back on
        h.Transport.RaiseDisconnected();   // and off again
        h.Clock.Advance(Grace);

        Assert.Equal(2, h.Reporter.Calls.Count);
    }

    [Fact]
    public async Task A_drop_outside_a_session_or_after_leaving_tells_the_teacher_nothing()
    {
        var h = new Harness();
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        h.Transport.RaiseDisconnected();
        h.Clock.Advance(Grace * 2);
        h.Transport.RaiseConnected();

        var sessionId = await h.JoinAsync();
        h.Transport.RaiseDisconnected();
        h.Hub.RaiseSessionEnded(sessionId);   // left within the grace period
        h.Clock.Advance(Grace * 2);

        Assert.Empty(h.Reporter.Calls);
    }

    [Fact]
    public async Task A_reporter_failure_does_not_undo_the_pause()
    {
        var h = new Harness();
        h.Reporter.Throw = true;
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        await h.JoinAsync();

        h.Transport.RaiseDisconnected();
        var ex = Record.Exception(() => h.Clock.Advance(Grace));

        Assert.Null(ex);
        Assert.True(h.Guard.IsBrowserPaused);
        Assert.Equal(1, h.PausedEvents);
    }

    // ---- #518: several Edge profiles ----

    [Fact]
    public async Task With_two_profiles_closing_one_tells_the_teacher_nothing()
    {
        using var h = new Harness(profiles: true);
        h.EdgeOnScreen();
        var (first, second) = await h.JoinWithTwoProfilesAsync();

        // The student closes Profile 1's last window: Edge ends that profile, its
        // link drops, its window is gone. Its preferences still have the extension on.
        h.AppIds.Close("Profile 1");
        h.Transport.RaiseDisconnected(second);
        h.Clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Empty(h.Reporter.Calls);
        Assert.Equal(ExtensionGuardState.Present, h.Guard.State);
        Assert.Equal(0, h.PausedEvents);
        Assert.Equal(new[] { first }, h.Witness.Connections);
    }

    [Fact]
    public async Task With_two_profiles_switching_the_extension_off_in_one_that_stays_open_tells_the_teacher_once_and_pauses_edge()
    {
        using var h = new Harness(profiles: true);
        h.EdgeOnScreen();
        var (_, second) = await h.JoinWithTwoProfilesAsync();

        // Switched off in Profile 1, whose window stays open: its link drops, the
        // Default profile's stays up, and Edge writes the change down 10 s later.
        h.Transport.RaiseDisconnected(second);
        h.Clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Empty(h.Reporter.Calls);
        h.Profiles.SwitchOff("Profile 1");
        h.Clock.Advance(Scan);

        var call = Assert.Single(h.Reporter.Calls);
        Assert.Equal(TamperKinds.ExtensionDisabled, call.Kind);
        Assert.True(h.Guard.IsBrowserPaused);
        Assert.Equal(1, h.PausedEvents);

        // It stays off: paused, and told once.
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True(h.Guard.IsBrowserPaused);
        Assert.Single(h.Reporter.Calls);
    }

    [Fact]
    public async Task With_two_profiles_removing_the_extension_from_one_that_stays_open_tells_the_teacher()
    {
        using var h = new Harness(profiles: true);
        h.EdgeOnScreen();
        var (_, second) = await h.JoinWithTwoProfilesAsync();

        h.Transport.RaiseDisconnected(second);
        h.Profiles.Remove("Profile 1");
        h.Clock.Advance(Scan);

        Assert.Single(h.Reporter.Calls);
        Assert.True(h.Guard.IsBrowserPaused);
    }

    [Fact]
    public async Task Switching_it_back_on_in_that_profile_lifts_the_pause()
    {
        using var h = new Harness(profiles: true);
        h.EdgeOnScreen();
        var (_, second) = await h.JoinWithTwoProfilesAsync();
        h.Transport.RaiseDisconnected(second);
        h.Profiles.SwitchOff("Profile 1");
        h.Clock.Advance(Scan);
        Assert.True(h.Guard.IsBrowserPaused);

        // The overlay's "switch it back on": Edge opens, the student switches it
        // on, its link comes up at once and the preferences follow 10 s later.
        Assert.True(h.Guard.BeginRepair());
        h.Transport.RaiseConnected(new WitnessConnection(3));
        Assert.Equal(ExtensionGuardState.Repairing, h.Guard.State);
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        h.Profiles.SwitchOn("Profile 1");
        h.Clock.Advance(Scan);

        Assert.Equal(ExtensionGuardState.Present, h.Guard.State);
        h.Clock.Advance(Repair * 2);
        Assert.Equal(ExtensionGuardState.Present, h.Guard.State);
        Assert.Single(h.Reporter.Calls);
    }

    [Fact]
    public async Task Closing_the_profile_the_extension_was_switched_off_in_lifts_the_pause()
    {
        using var h = new Harness(profiles: true);
        h.EdgeOnScreen();
        var (_, second) = await h.JoinWithTwoProfilesAsync();
        h.Transport.RaiseDisconnected(second);
        h.Profiles.SwitchOff("Profile 1");
        h.Clock.Advance(Scan);
        Assert.True(h.Guard.IsBrowserPaused);

        h.AppIds.Close("Profile 1");
        h.Clock.Advance(Scan);

        Assert.Equal(ExtensionGuardState.Present, h.Guard.State);
    }

    [Fact]
    public async Task A_profile_just_switched_back_on_is_not_taken_for_off_while_its_preferences_lag()
    {
        // Off before the lesson and switched on right at its start: the link is
        // up, but for 10 s the preferences still say "off".
        using var h = new Harness(profiles: true);
        h.EdgeOnScreen();
        h.Profiles.SwitchOff("Profile 1");
        h.Transport.RaiseConnected(new WitnessConnection(1));
        await h.JoinAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(h.Guard.IsBrowserPaused);   // open, and really off
        Assert.Single(h.Reporter.Calls);

        h.Guard.BeginRepair();
        h.Transport.RaiseConnected(new WitnessConnection(2));
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        h.Profiles.SwitchOn("Profile 1");
        h.Clock.Advance(Scan);
        Assert.Equal(ExtensionGuardState.Present, h.Guard.State);

        // A restart of that extension right after (an option change): the link
        // drops and is back, and a write of the preferences caught in between
        // still says "off" for a moment.
        h.Transport.RaiseDisconnected(new WitnessConnection(2));
        h.Profiles.SwitchOff("Profile 1");
        h.Transport.RaiseConnected(new WitnessConnection(3));
        h.Clock.Advance(Scan);
        h.Profiles.SwitchOn("Profile 1");
        h.Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(ExtensionGuardState.Present, h.Guard.State);
        Assert.Single(h.Reporter.Calls);
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

    private sealed class Harness : IDisposable
    {
        public FakeHub Hub { get; } = new();
        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UnixEpoch);
        public FakeWitnessTransport Transport { get; } = new();
        public FakeWindows Windows { get; } = new();
        public SessionCoordinator Coordinator { get; }
        public ExtensionWitnessMonitor Witness { get; }
        public ExtensionGuard Guard { get; }
        public RecordingReporter Reporter { get; } = new();
        public FakeAppIds AppIds { get; } = new();
        public FakeEdgeProfiles Profiles { get; } = new();
        public int PausedEvents { get; private set; }

        /// <param name="profiles">
        /// With the watch on the open Edge profiles (#518), over a user data folder
        /// of its own with a Default profile and a Profile 1, both with the
        /// extension on and a window open.
        /// </param>
        public Harness(bool profiles = false)
        {
            Coordinator = new SessionCoordinator(
                Hub,
                new ConfirmingUi(),
                Options.Create(new RealtimeSettings { JoinConfirmationDuration = TimeSpan.FromSeconds(5) }),
                Clock);
            Witness = new ExtensionWitnessMonitor(Transport);
            var settings = Options.Create(new SessionSettings
            {
                ExtensionGraceSeconds = (int)Grace.TotalSeconds,
                ExtensionRepairSeconds = (int)Repair.TotalSeconds,
                ExtensionProfileScanSeconds = (int)Scan.TotalSeconds,
            });
            OpenProfileExtensionWatch? watch = null;
            if (profiles)
            {
                Profiles.SwitchOn("Default");
                Profiles.SwitchOn("Profile 1");
                AppIds.Open("Default");
                AppIds.Open("Profile 1");
                watch = new OpenProfileExtensionWatch(
                    Coordinator,
                    Witness,
                    new EdgeOpenProfileExtensionProbe(Profiles.UserData, AppIds, "msedge", new[] { FakeEdgeProfiles.ExtensionId }),
                    settings,
                    Clock);
            }
            Guard = new ExtensionGuard(
                Coordinator,
                Witness,
                Windows,
                Reporter,
                settings,
                Options.Create(new WitnessSettings()),
                watch,
                Clock);
            Guard.BrowserPaused += (_, _) => PausedEvents++;
        }

        /// <summary>
        /// Both profiles' extensions connected long before the lesson, and the
        /// session joined and under way.
        /// </summary>
        public async Task<(WitnessConnection Default, WitnessConnection Profile1)> JoinWithTwoProfilesAsync()
        {
            var first = new WitnessConnection(1);
            var second = new WitnessConnection(2);
            Transport.RaiseConnected(first);
            Transport.RaiseConnected(second);
            Clock.Advance(TimeSpan.FromMinutes(1));
            await JoinAsync();
            Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(ExtensionGuardState.Present, Guard.State);
            return (first, second);
        }

        public void Dispose() => Profiles.Dispose();

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
        // One Edge profile's extension, unless a test names another.
        private static readonly WitnessConnection Only = new(1);
        public event EventHandler<WitnessConnection>? WitnessConnected;
        public event EventHandler<WitnessConnection>? WitnessDisconnected;
        public void RaiseConnected(WitnessConnection? connection = null) => WitnessConnected?.Invoke(this, connection ?? Only);
        public void RaiseDisconnected(WitnessConnection? connection = null) => WitnessDisconnected?.Invoke(this, connection ?? Only);
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingReporter : ITamperReporter
    {
        public List<(Guid SessionId, string Kind)> Calls { get; } = new();
        public bool Throw { get; set; }

        public Task ReportAsync(Guid sessionId, string kind, CancellationToken ct = default)
        {
            Calls.Add((sessionId, kind));
            if (Throw) throw new InvalidOperationException("simulated hub failure");
            return Task.CompletedTask;
        }
    }

    /// <summary>The AppUserModelIDs of Edge's open windows, as Edge names them per profile.</summary>
    private sealed class FakeAppIds : IBrowserWindowAppIds
    {
        private readonly HashSet<string> _open = new();
        public void Open(string profile) => _open.Add(profile);
        public void Close(string profile) => _open.Remove(profile);

        public IReadOnlyList<string> GetWindowAppIds(string processName) =>
            _open.Select(profile => profile == "Default" ? "MSEdge" : $"MSEdge.UserData.{profile.Replace(" ", "")}").ToList();
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
        public event EventHandler<AllowlistAmendedPayload>? AllowlistAmended;
        public event EventHandler<UnblockDeniedPayload>? UnblockDenied;
#pragma warning restore CS0067
        public event EventHandler<Guid>? SessionEnded;

        public void RaiseSessionEnded(Guid sessionId) => SessionEnded?.Invoke(this, sessionId);

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task JoinSessionAsync(Guid sessionId, string? joinCode, CancellationToken ct = default) => Task.CompletedTask;
        public Task LeaveSessionAsync(Guid sessionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeclineSessionAsync(Guid sessionId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReportEventAsync(Guid sessionId, string kind, string payloadJson, DateTimeOffset? occurredAt = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ExtensionHeartbeatAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> HeartbeatAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> IsInSessionAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<SessionStartedPayload?> GetStartedSessionAsync(CancellationToken ct = default) => Task.FromResult<SessionStartedPayload?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
