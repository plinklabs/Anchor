using FocusAgent.Core.Auth;
using System.Text.Json;
using FocusAgent.Core.Dtos;
using FocusAgent.Core.Extension;
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
    private static readonly TimeSpan SiteAccessReport = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SchoolScan = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SchoolReport = TimeSpan.FromSeconds(60);
    private const string SchoolAddress = "ada.lovelace@student.school.be";
    private const string SchoolOid = "28c4a3c2-1f0e-4f55-9a7c-6d3f0a1b2c3d";

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

    // ---- the extension's own settings (#524) --------------------------------

    [Fact]
    public async Task An_extension_without_site_access_pauses_edge_at_once_without_telling_the_teacher_yet()
    {
        var h = new Harness(selfCheck: true);
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        var session = await h.JoinAsync();

        h.Transport.Check(session, allSites: false);

        // It said so itself: no grace period to wait out.
        Assert.True(h.Guard.IsBrowserPaused);
        Assert.Equal(ExtensionPauseReason.NoSiteAccess, h.Guard.PauseReason);
        Assert.Equal(1, h.PausedEvents);
        // Not "switched off or removed": the extension is on.
        Assert.Empty(h.Reporter.Calls);
    }

    [Fact]
    public async Task Site_access_put_back_lifts_the_pause_at_once_and_nothing_reaches_the_teacher()
    {
        var h = new Harness(selfCheck: true);
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        var session = await h.JoinAsync();
        h.Transport.Check(session, allSites: false);

        h.Clock.Advance(SiteAccessReport - TimeSpan.FromSeconds(1));
        h.Transport.Check(session, allSites: true);

        Assert.Equal(ExtensionGuardState.Present, h.Guard.State);
        Assert.Equal(ExtensionPauseReason.ExtensionOff, h.Guard.PauseReason);
        h.Clock.Advance(SiteAccessReport * 2);
        Assert.Empty(h.Reporter.Calls);
    }

    [Fact]
    public async Task Site_access_not_put_back_in_time_is_told_as_site_access_and_edge_stays_paused()
    {
        var h = new Harness(selfCheck: true);
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        var session = await h.JoinAsync();
        h.Transport.Check(session, allSites: false);

        h.Clock.Advance(SiteAccessReport);

        Assert.Equal(new[] { (session, TamperKinds.HostPermissionRevoked) }, h.Reporter.Calls);
        Assert.True(h.Guard.IsBrowserPaused);
        Assert.Equal(1, h.PausedEvents);
    }

    [Fact]
    public async Task The_repair_action_opens_edge_and_has_the_extension_open_its_own_page()
    {
        var h = new Harness(selfCheck: true);
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        var session = await h.JoinAsync();
        h.Transport.Check(session, allSites: false);
        h.Transport.Sent.Clear();

        Assert.True(h.Guard.BeginRepair());

        Assert.Equal(ExtensionGuardState.Repairing, h.Guard.State);
        Assert.Equal(ExtensionPauseReason.NoSiteAccess, h.Guard.PauseReason);
        var asked = Assert.Single(h.Transport.Sent);
        using var line = JsonDocument.Parse(asked.Line);
        Assert.Equal("open_extension_page", line.RootElement.GetProperty("type").GetString());
        Assert.Equal(session, line.RootElement.GetProperty("sessionId").GetGuid());

        // Put back on the page it opened: the pause is over.
        h.Transport.Check(session, allSites: true);
        Assert.Equal(ExtensionGuardState.Present, h.Guard.State);
        h.Clock.Advance(Repair * 2);
        Assert.Equal(ExtensionGuardState.Present, h.Guard.State);
    }

    [Fact]
    public async Task The_repair_action_for_a_switched_off_extension_asks_no_extension_to_open_a_page()
    {
        var h = new Harness(selfCheck: true);
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        await h.JoinAsync();
        h.Transport.RaiseDisconnected();
        h.Clock.Advance(Grace);
        h.Transport.Sent.Clear();

        Assert.True(h.Guard.BeginRepair());

        Assert.Equal(ExtensionPauseReason.ExtensionOff, h.Guard.PauseReason);
        Assert.Empty(h.Transport.Sent);
    }

    [Fact]
    public async Task A_repair_window_that_runs_out_without_site_access_pauses_edge_again_for_it()
    {
        var h = new Harness(selfCheck: true);
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        var session = await h.JoinAsync();
        h.Transport.Check(session, allSites: false);
        h.Guard.BeginRepair();

        h.Clock.Advance(Repair);

        Assert.True(h.Guard.IsBrowserPaused);
        Assert.Equal(ExtensionPauseReason.NoSiteAccess, h.Guard.PauseReason);
        Assert.Equal(2, h.PausedEvents);
        Assert.DoesNotContain(h.Reporter.Calls, c => c.Kind == TamperKinds.ExtensionDisabled);
    }

    [Fact]
    public async Task The_extension_switched_off_while_paused_for_site_access_is_paused_and_told_as_switched_off()
    {
        var h = new Harness(selfCheck: true);
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        var session = await h.JoinAsync();
        h.Transport.Check(session, allSites: false);

        h.Transport.RaiseDisconnected();
        h.Clock.Advance(Grace);

        Assert.True(h.Guard.IsBrowserPaused);
        Assert.Equal(ExtensionPauseReason.ExtensionOff, h.Guard.PauseReason);
        Assert.Equal(new[] { (session, TamperKinds.ExtensionDisabled) }, h.Reporter.Calls);
    }

    [Fact]
    public async Task An_extension_back_after_a_restart_without_site_access_is_paused_for_site_access()
    {
        // An option changed on its page restarts the extension: the link drops,
        // comes back, and the session handed down again brings the check.
        var h = new Harness(selfCheck: true);
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        var session = await h.JoinAsync();

        h.Transport.RaiseDisconnected();
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Transport.RaiseConnected();
        Assert.Equal(ExtensionGuardState.Present, h.Guard.State);
        h.Transport.Check(session, allSites: false, inPrivate: true);

        Assert.True(h.Guard.IsBrowserPaused);
        Assert.Equal(ExtensionPauseReason.NoSiteAccess, h.Guard.PauseReason);
    }

    [Fact]
    public async Task Paused_for_site_access_then_switched_off_in_an_open_profile_is_paused_and_told_as_switched_off()
    {
        using var h = new Harness(profiles: true, selfCheck: true);
        h.EdgeOnScreen();
        var (first, second) = await h.JoinWithTwoProfilesAsync();
        var session = h.Coordinator.JoinedSessionId!.Value;
        h.Transport.Check(session, allSites: false, from: first);
        Assert.Equal(ExtensionPauseReason.NoSiteAccess, h.Guard.PauseReason);

        h.Transport.RaiseDisconnected(second);
        h.Profiles.SwitchOff("Profile 1");
        h.Clock.Advance(Scan * 3);

        Assert.True(h.Guard.IsBrowserPaused);
        Assert.Equal(ExtensionPauseReason.ExtensionOff, h.Guard.PauseReason);
        Assert.Contains((session, TamperKinds.ExtensionDisabled), h.Reporter.Calls);
    }

    [Fact]
    public async Task A_check_without_site_access_that_beats_the_join_pauses_edge_once_the_session_is_joined()
    {
        var h = new Harness(selfCheck: true);
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        var session = Guid.NewGuid();
        h.Transport.Check(session, allSites: false);
        Assert.Equal(ExtensionGuardState.Off, h.Guard.State);

        await h.JoinAsync(session);

        Assert.True(h.Guard.IsBrowserPaused);
        Assert.Equal(ExtensionPauseReason.NoSiteAccess, h.Guard.PauseReason);
        Assert.Equal(1, h.PausedEvents);
    }

    [Fact]
    public async Task An_extension_not_allowed_in_InPrivate_pauses_nothing()
    {
        // Off by default on every install: pausing for it would pause every laptop.
        var h = new Harness(selfCheck: true);
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        var session = await h.JoinAsync();

        h.Transport.Check(session, allSites: true, inPrivate: false);
        h.Clock.Advance(SiteAccessReport * 2);

        Assert.Equal(ExtensionGuardState.Present, h.Guard.State);
        Assert.Equal(0, h.PausedEvents);
        Assert.Empty(h.Reporter.Calls);
    }

    [Fact]
    public async Task With_two_profiles_the_pause_holds_until_every_connected_extension_may_work_on_all_sites()
    {
        var h = new Harness(selfCheck: true);
        h.EdgeOnScreen();
        var first = new WitnessConnection(1);
        var second = new WitnessConnection(2);
        h.Transport.RaiseConnected(first);
        h.Transport.RaiseConnected(second);
        var session = await h.JoinAsync();

        h.Transport.Check(session, allSites: false, from: first);
        h.Transport.Check(session, allSites: false, from: second);
        h.Transport.Check(session, allSites: true, from: first);
        Assert.True(h.Guard.IsBrowserPaused);

        // The other profile's last window closes, and its extension with it.
        h.Transport.RaiseDisconnected(second);
        Assert.Equal(ExtensionGuardState.Present, h.Guard.State);
    }

    // ---- the school profile (#525) --------------------------------------------

    [Fact]
    public async Task Without_a_school_profile_edge_is_paused_at_once_without_telling_the_teacher_yet()
    {
        using var h = new Harness(schoolProfile: true);
        h.Profiles.WriteLocalState(("Default", "ada.personal@outlook.com", null));
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        await h.JoinAsync();

        h.FirstSchoolScan();

        Assert.True(h.Guard.IsBrowserPaused);
        Assert.Equal(ExtensionPauseReason.NoSchoolProfile, h.Guard.PauseReason);
        Assert.Equal(1, h.PausedEvents);
        // Not "switched off or removed": the extension is on.
        Assert.Empty(h.Reporter.Calls);
    }

    [Fact]
    public async Task Signing_in_during_the_repair_window_lifts_the_pause_and_nothing_reaches_the_teacher()
    {
        using var h = new Harness(schoolProfile: true);
        h.Profiles.WriteLocalState(("Default", "", null));
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        await h.JoinAsync();
        h.FirstSchoolScan();

        // The overlay's button: Edge opens so the student can sign in.
        Assert.True(h.Guard.BeginRepair());
        Assert.Equal(ExtensionGuardState.Repairing, h.Guard.State);
        Assert.Equal(ExtensionPauseReason.NoSchoolProfile, h.Guard.PauseReason);

        h.Clock.Advance(TimeSpan.FromSeconds(20));
        h.Profiles.WriteLocalState(("Default", SchoolAddress, SchoolOid));
        h.Clock.Advance(SchoolScan);

        Assert.Equal(ExtensionGuardState.Present, h.Guard.State);
        h.Clock.Advance(Repair + SchoolReport);
        Assert.Equal(ExtensionGuardState.Present, h.Guard.State);
        Assert.Empty(h.Reporter.Calls);
    }

    [Fact]
    public async Task No_school_profile_in_time_is_told_as_such_and_a_repair_window_that_runs_out_pauses_edge_again()
    {
        using var h = new Harness(schoolProfile: true);
        h.Profiles.WriteLocalState(("Default", "", null));
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        var session = await h.JoinAsync();
        h.FirstSchoolScan();
        Assert.True(h.Guard.BeginRepair());

        h.Run(Repair);

        Assert.True(h.Guard.IsBrowserPaused);
        Assert.Equal(ExtensionPauseReason.NoSchoolProfile, h.Guard.PauseReason);
        Assert.Equal(new[] { (session, TamperKinds.SchoolProfileMissing) }, h.Reporter.Calls);
    }

    [Fact]
    public async Task No_school_profile_comes_before_site_access_and_site_access_shows_again_once_there_is_one()
    {
        using var h = new Harness(selfCheck: true, schoolProfile: true);
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        var session = await h.JoinAsync();
        h.FirstSchoolScan();
        h.Transport.Check(session, allSites: false);
        Assert.Equal(ExtensionPauseReason.NoSiteAccess, h.Guard.PauseReason);

        // The student signs out of Edge: which profile is the school profile decides
        // which extension matters, so that is what the pause says now.
        h.Profiles.WriteLocalState(("Default", "", null));
        h.Clock.Advance(SchoolScan);
        Assert.True(h.Guard.IsBrowserPaused);
        Assert.Equal(ExtensionPauseReason.NoSchoolProfile, h.Guard.PauseReason);

        h.Profiles.WriteLocalState(("Default", SchoolAddress, SchoolOid));
        h.Clock.Advance(SchoolScan);
        Assert.True(h.Guard.IsBrowserPaused);
        Assert.Equal(ExtensionPauseReason.NoSiteAccess, h.Guard.PauseReason);
    }

    [Fact]
    public async Task A_device_where_the_extension_never_connected_is_not_paused_for_a_school_profile()
    {
        using var h = new Harness(schoolProfile: true);
        h.Profiles.WriteLocalState(("Default", "", null));
        h.EdgeOnScreen();
        await h.JoinAsync();

        h.FirstSchoolScan();
        h.Clock.Advance(SchoolReport * 2);

        Assert.Equal(ExtensionGuardState.Off, h.Guard.State);
        Assert.Equal(0, h.PausedEvents);
        Assert.Empty(h.Reporter.Calls);
    }

    [Fact]
    public async Task A_link_back_up_without_a_school_profile_is_paused_for_it()
    {
        using var h = new Harness(schoolProfile: true);
        h.Profiles.WriteLocalState(("Default", "", null));
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        await h.JoinAsync();
        h.Transport.RaiseDisconnected();
        h.FirstSchoolScan();
        // The link is down: that is handled first, after its grace period.
        Assert.Equal(ExtensionGuardState.Waiting, h.Guard.State);

        h.Transport.RaiseConnected();

        Assert.True(h.Guard.IsBrowserPaused);
        Assert.Equal(ExtensionPauseReason.NoSchoolProfile, h.Guard.PauseReason);
    }

    // ---- the school profile without the extension (#536) ----------------------

    [Fact]
    public async Task A_school_profile_without_the_extension_pauses_edge_for_that_and_the_button_opens_the_store_listing_in_it()
    {
        using var h = new Harness(schoolProfile: true);
        h.Profiles.Remove("Default");
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        await h.JoinAsync();

        h.FirstSchoolScan();

        Assert.True(h.Guard.IsBrowserPaused);
        Assert.Equal(ExtensionPauseReason.SchoolProfileWithoutExtension, h.Guard.PauseReason);
        Assert.Equal(1, h.PausedEvents);
        Assert.Empty(h.Reporter.Calls);

        // The overlay's button: Edge opens at Anchor's store listing, in the profile
        // signed in with the school account.
        Assert.True(h.Guard.BeginRepair());
        Assert.Equal(ExtensionGuardState.Repairing, h.Guard.State);
        Assert.Equal(ExtensionPauseReason.SchoolProfileWithoutExtension, h.Guard.PauseReason);
        Assert.Equal(new[] { ("Default", (string?)EdgeExtensionPolicy.StoreListingUrl) }, h.Launcher.Opened);

        // The student adds Anchor there: the pause is over, and the teacher hears nothing.
        h.Clock.Advance(TimeSpan.FromSeconds(20));
        h.Profiles.SwitchOn("Default");
        h.Clock.Advance(SchoolScan);

        Assert.Equal(ExtensionGuardState.Present, h.Guard.State);
        h.Run(Repair + SchoolReport);
        Assert.Equal(ExtensionGuardState.Present, h.Guard.State);
        Assert.Empty(h.Reporter.Calls);
    }

    [Fact]
    public async Task The_button_of_the_other_pauses_opens_no_store_listing()
    {
        using var h = new Harness(schoolProfile: true);
        h.Profiles.WriteLocalState(("Default", "", null));
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        await h.JoinAsync();
        h.FirstSchoolScan();
        Assert.Equal(ExtensionPauseReason.NoSchoolProfile, h.Guard.PauseReason);

        Assert.True(h.Guard.BeginRepair());

        Assert.Empty(h.Launcher.Opened);
    }

    [Fact]
    public async Task Signing_in_to_a_profile_without_the_extension_while_paused_changes_what_the_pause_says()
    {
        using var h = new Harness(schoolProfile: true);
        h.Profiles.Remove("Default");
        h.Profiles.WriteLocalState(("Default", "", null));
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        await h.JoinAsync();
        h.FirstSchoolScan();
        Assert.Equal(ExtensionPauseReason.NoSchoolProfile, h.Guard.PauseReason);
        Assert.Equal(1, h.PausedEvents);

        h.Profiles.WriteLocalState(("Default", SchoolAddress, SchoolOid));
        h.Clock.Advance(SchoolScan);

        // Paused anew, so the overlay says what is missing now.
        Assert.True(h.Guard.IsBrowserPaused);
        Assert.Equal(ExtensionPauseReason.SchoolProfileWithoutExtension, h.Guard.PauseReason);
        Assert.Equal(2, h.PausedEvents);

        // A scan that finds the same pauses nothing again.
        h.Clock.Advance(SchoolScan);
        Assert.Equal(2, h.PausedEvents);

        h.Profiles.WriteLocalState(("Default", "", null));
        h.Clock.Advance(SchoolScan);
        Assert.Equal(ExtensionPauseReason.NoSchoolProfile, h.Guard.PauseReason);
        Assert.Equal(3, h.PausedEvents);
        Assert.Empty(h.Reporter.Calls);
    }

    [Fact]
    public async Task Signing_in_during_the_repair_window_to_a_profile_without_the_extension_pauses_for_that_once_it_runs_out()
    {
        using var h = new Harness(schoolProfile: true);
        h.Profiles.Remove("Default");
        h.Profiles.WriteLocalState(("Default", "", null));
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        await h.JoinAsync();
        h.FirstSchoolScan();
        Assert.True(h.Guard.BeginRepair());

        h.Clock.Advance(TimeSpan.FromSeconds(20));
        h.Profiles.WriteLocalState(("Default", SchoolAddress, SchoolOid));
        h.Clock.Advance(SchoolScan);
        // The student is in Edge: the repair window runs its course.
        Assert.Equal(ExtensionGuardState.Repairing, h.Guard.State);

        h.Run(Repair);

        Assert.True(h.Guard.IsBrowserPaused);
        Assert.Equal(ExtensionPauseReason.SchoolProfileWithoutExtension, h.Guard.PauseReason);
    }

    [Fact]
    public async Task A_school_profile_still_without_the_extension_is_told_as_such_and_edge_paused_again_after_the_repair_window()
    {
        using var h = new Harness(schoolProfile: true);
        h.Profiles.Remove("Default");
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        var session = await h.JoinAsync();
        h.FirstSchoolScan();
        Assert.True(h.Guard.BeginRepair());

        h.Run(Repair);

        Assert.True(h.Guard.IsBrowserPaused);
        Assert.Equal(ExtensionPauseReason.SchoolProfileWithoutExtension, h.Guard.PauseReason);
        Assert.Equal(new[] { (session, TamperKinds.SchoolProfileWithoutExtension) }, h.Reporter.Calls);
    }

    [Fact]
    public async Task Leaving_the_session_while_paused_for_site_access_lifts_the_pause()
    {
        var h = new Harness(selfCheck: true);
        h.EdgeOnScreen();
        h.Transport.RaiseConnected();
        var session = await h.JoinAsync();
        h.Transport.Check(session, allSites: false);

        h.Hub.RaiseSessionEnded(session);

        Assert.Equal(ExtensionGuardState.Off, h.Guard.State);
        h.Clock.Advance(SiteAccessReport * 2);
        Assert.Empty(h.Reporter.Calls);
    }

    /// <summary>What the repair action opened in which Edge profile (#536).</summary>
    private sealed class RecordingProfileLauncher : IBrowserProfileLauncher
    {
        public List<(string Profile, string? Url)> Opened { get; } = new();
        public void CloseWindow(nint windowHandle) { }
        public void OpenProfile(string profile, string? url = null) => Opened.Add((profile, url));
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
        public ExtensionSelfCheckWatch? SelfCheck { get; }
        public SchoolProfileGuard? SchoolProfile { get; }
        public RecordingProfileLauncher Launcher { get; } = new();
        public int PausedEvents { get; private set; }

        /// <param name="profiles">
        /// With the watch on the open Edge profiles (#518), over a user data folder
        /// of its own with a Default profile and a Profile 1, both with the
        /// extension on and a window open.
        /// </param>
        /// <param name="selfCheck">
        /// With the watch on what the extension says about its own settings (#524),
        /// reporting through <see cref="Reporter"/> after <see cref="SiteAccessReport"/>.
        /// </param>
        /// <param name="schoolProfile">
        /// With the guard on the school profile (#525), over the same user data
        /// folder: a Default profile with the extension on, signed in with the
        /// school account (rewrite its Local State to sign it out), scanned every
        /// <see cref="SchoolScan"/> and reporting through <see cref="Reporter"/>
        /// after <see cref="SchoolReport"/>.
        /// </param>
        public Harness(bool profiles = false, bool selfCheck = false, bool schoolProfile = false)
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
                ExtensionSiteAccessReportSeconds = (int)SiteAccessReport.TotalSeconds,
                SchoolProfileScanSeconds = (int)SchoolScan.TotalSeconds,
                SchoolProfileReportSeconds = (int)SchoolReport.TotalSeconds,
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
            if (selfCheck)
                SelfCheck = new ExtensionSelfCheckWatch(Transport, Coordinator, Reporter, settings, Clock);
            if (schoolProfile)
            {
                Profiles.SwitchOn("Default");
                Profiles.WriteLocalState(("Default", SchoolAddress, SchoolOid));
                SchoolProfile = new SchoolProfileGuard(
                    Coordinator,
                    Witness,
                    new EdgeSchoolProfileProbe(
                        Profiles.UserData,
                        new EdgeOpenProfileExtensionProbe(Profiles.UserData, AppIds, "msedge", new[] { FakeEdgeProfiles.ExtensionId })),
                    new SchoolProfileGuardTests.FakeWindowReader(),
                    new SignedInAccount { Current = new SchoolAccount(SchoolAddress, SchoolOid) },
                    Reporter,
                    settings,
                    Options.Create(new WitnessSettings()),
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
                Clock,
                selfCheck: SelfCheck,
                schoolProfile: SchoolProfile,
                browserLauncher: Launcher);
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

        public void Dispose()
        {
            SchoolProfile?.Dispose();
            Profiles.Dispose();
        }

        /// <summary>The first school-profile scan, one interval after the join (#525).</summary>
        public void FirstSchoolScan() => Clock.Advance(SchoolScan);

        /// <summary>
        /// Lets <paramref name="duration"/> pass a second at a time: each scan reads
        /// the clock, and one long advance would hand them all its end.
        /// </summary>
        public void Run(TimeSpan duration)
        {
            for (var left = duration; left > TimeSpan.Zero; left -= TimeSpan.FromSeconds(1))
                Clock.Advance(left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1));
        }

        public void EdgeOnScreen() =>
            Windows.Windows = new[]
            {
                new OpenWindow(0x10, new AppInfo("winword", null, null)),
                new OpenWindow(0x20, new AppInfo("msedge", null, null)),
            };

        public void NoEdgeOnScreen() =>
            Windows.Windows = new[] { new OpenWindow(0x10, new AppInfo("winword", null, null)) };

        public async Task<Guid> JoinAsync(Guid? sessionId = null)
        {
            var payload = new SessionStartedPayload(
                SessionId: sessionId ?? Guid.NewGuid(),
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

    /// <summary>
    /// The witness link: its connections, and (for the self-check, #524) what an
    /// extension says over it and what the agent sends back.
    /// </summary>
    private sealed class FakeWitnessTransport : IExtensionWitnessChannel
    {
        // One Edge profile's extension, unless a test names another.
        private static readonly WitnessConnection Only = new(1);
        private readonly HashSet<long> _connected = new();
        public event EventHandler<WitnessConnection>? WitnessConnected;
        public event EventHandler<WitnessConnection>? WitnessDisconnected;
        public event EventHandler<WitnessMessage>? MessageReceived;

        /// <summary>The lines sent to an extension, with the connection they went to.</summary>
        public List<(long To, string Line)> Sent { get; } = new();

        public void RaiseConnected(WitnessConnection? connection = null)
        {
            connection ??= Only;
            _connected.Add(connection.Id);
            WitnessConnected?.Invoke(this, connection);
        }

        public void RaiseDisconnected(WitnessConnection? connection = null)
        {
            connection ??= Only;
            _connected.Remove(connection.Id);
            WitnessDisconnected?.Invoke(this, connection);
        }

        /// <summary>The extension's self-check, as background.ts sends it (#524).</summary>
        public void Check(Guid sessionId, bool allSites, bool inPrivate = false, WitnessConnection? from = null) =>
            MessageReceived?.Invoke(this, new WitnessMessage(
                from ?? Only,
                JsonSerializer.Serialize(new { type = "self_check", sessionId, allSites, inPrivate })));

        public bool TrySend(WitnessConnection connection, string line)
        {
            if (!_connected.Contains(connection.Id)) return false;
            Sent.Add((connection.Id, line));
            return true;
        }

        public int Broadcast(string line)
        {
            foreach (var id in _connected)
                Sent.Add((id, line));
            return _connected.Count;
        }

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
