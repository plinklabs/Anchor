using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Asserting e2e for #556: the extension is installed in the Edge profile the
/// student has open, but its link to the agent never comes up. In the classroom
/// test of 2026-11-20 that left the session unfiltered and silent: the extension
/// gets its session only over the link, and the agent's guard stayed Off because
/// the link had never been up since the agent started. Now the agent sees on disk
/// that the extension is installed in an open profile, waits the grace period,
/// wakes it by opening Edge in that profile at a page the session allows, waits
/// once more, then pauses Edge and tells the teacher it isn't connected (not that
/// it was switched off). A link that comes up at last lifts the pause. The
/// overlay's button opens that profile again and brings no other Edge window
/// forward (#597). A device without the extension in any profile is still left
/// alone.
///
/// The real agent, backend and witness pipe, with the stand-ins of
/// <see cref="ExtensionOffInOneProfileTests"/>: a <c>User Data</c> folder of the
/// spec's own with the extension installed in the Default profile, the spec's own
/// window as that profile's browser window (its AppUserModelID set as Edge sets
/// it), and a fake extension on a private pipe that stays away until the end.
/// The browser is a stand-in, so the agent never starts Edge: what it would have
/// opened is read from /status.
/// </summary>
[Collection(AgentE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class ExtensionNeverConnectedTests
{
    private const string StoreExtensionId = "dnkimhodjfogjibnbbfdjdapgmmiojio";
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(6);

    private readonly BackendFixture _backend;
    public ExtensionNeverConnectedTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task An_installed_extension_that_never_connects_is_woken_then_edge_is_paused_and_the_teacher_told()
    {
        var api = new BackendClient(_backend.Url);
        var stale = await api.GetRejoinableSessionIdsAsync();
        Assert.True(
            stale.Count == 0,
            $"The student is still in running session(s) {string.Join(", ", stale)} that an earlier spec left " +
            "behind; this agent would rejoin them on connect. End every session a spec starts.");

        var root = Path.Combine(Path.GetTempPath(), "anchor-e2e-edge-" + Guid.NewGuid().ToString("N"));
        var userData = Path.Combine(root, "User Data");
        WriteInstalledExtension(userData, "Default");

        var pipeName = "anchor-witness-e2e-" + Guid.NewGuid().ToString("N");
        using var self = Process.GetCurrentProcess();
        await using var agent = AgentProcess.Launch(
            _backend.Url,
            TestConfig.StudentOid,
            autoJoin: true,
            extraEnvironment: new Dictionary<string, string>
            {
                ["Witness__PipeName"] = pipeName,
                // The spec's own window plays Edge's.
                ["Witness__BrowserProcessName"] = self.ProcessName,
                ["Witness__BrowserUserDataFolder"] = userData,
                ["Session__ExtensionGraceSeconds"] = ((int)Grace.TotalSeconds).ToString(),
            });
        await using var extension = new FakeExtensionWitness(pipeName);
        OffListWindow? window = null;
        Guid? sessionId = null;
        try
        {
            await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));

            var classId = await api.FindClassIdAsync();
            var session = await api.StartSessionAsync(classId);
            sessionId = session.Id;

            // 1. The join: guarded, not Off (Off was the silent case).
            var joined = await agent.WaitForAsync(
                s => s.JoinedSessionId == session.Id && s.StartupSweep is not null && s.ExtensionGuard is not null,
                TimeSpan.FromSeconds(10));
            Assert.True(
                joined is { ExtensionGuard.State: "Waiting" },
                $"The guard didn't wait for the installed extension at the join (guard: {joined?.ExtensionGuard}).");

            // The Default profile's window comes up on screen, with the extension
            // installed and on there, and no link: the extension never connects.
            // After the join's sweep, which would minimize a window already open
            // (the stand-in isn't an allowed app), and without taking the
            // foreground, which would have it blocked.
            window = await OffListWindow.OpenAsync("Anchor e2e Edge (Default)", TimeSpan.FromSeconds(5), activate: false);
            WindowAppId.Set(window.Handle, "MSEdge");

            // 2. The grace period runs out: the agent wakes the extension, opening
            //    the profile it is installed in at an http(s) page. Nothing paused yet.
            var woken = await agent.WaitForAsync(
                s => s.SchoolProfile?.LastOpened is { Profile: "Default" }, Grace + TimeSpan.FromSeconds(10));
            var opened = woken?.SchoolProfile?.LastOpened;
            Assert.True(
                opened is { Profile: "Default" },
                $"The agent did not open the profile with the extension to wake it (last opened: {opened}).");
            Assert.StartsWith("https://", opened!.Url);
            Assert.NotEqual("Paused", woken!.ExtensionGuard?.State);
            Assert.Equal(0, await TamperReports.CountAsync(api, session.Id, "extension_not_connected"));

            // 3. Still no link after another grace period: Edge is paused for it, and
            //    the teacher is told it isn't connected, once, not "switched off".
            await WaitForGuardAsync(
                agent, g => g is { State: "Paused", PauseReason: "NotConnected" }, Grace + TimeSpan.FromSeconds(10),
                "the browser to be paused for an extension that isn't connected");
            Assert.True(
                await TamperReports.WaitForAsync(api, session.Id, "extension_not_connected", 1, TimeSpan.FromSeconds(10)),
                "Edge was paused for an extension that never connected, but the teacher was not told.");
            Assert.Equal(0, await TamperReports.CountAsync(api, session.Id, "extension_disabled"));

            // 4. The link comes up at last: the pause lifts, and nothing more is told.
            await extension.ConnectAsync();
            await WaitForGuardAsync(
                agent, g => g is { WitnessConnected: true, State: "Present" }, TimeSpan.FromSeconds(10), "the pause to lift");
            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.Equal(1, await TamperReports.CountAsync(api, session.Id, "extension_not_connected"));
        }
        finally
        {
            if (sessionId is { } id)
            {
                try { await api.EndSessionAsync(id); } catch { /* best-effort */ }
            }
            window?.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task The_overlays_button_opens_the_woken_profile_again_and_brings_no_other_edge_window_forward()
    {
        // #597: Edge is open in the school profile (Default), where the extension is
        // installed but never connects, and in a personal profile (Profile 1) that
        // never had it. Edge is paused as not connected. The overlay's button opens
        // Edge in the profile the extension was woken in; real Edge brings that
        // profile's window forward itself. The overlay must bring no Edge window
        // forward after it: "Edge" can be the personal profile's window, which the
        // school-profile rule then pauses, and the student gets a second pause
        // instead of their school profile.
        var api = new BackendClient(_backend.Url);
        await using var run = await SchoolProfileRun.StartAsync(
            _backend.Url, api, signedIn: true, extensionConnects: false, personalProfileHasExtension: false,
            grace: Grace);
        var school = await run.OpenWindowAsync("Anchor e2e - School - Microsoft Edge", "MSEdge");
        var personal = await run.OpenWindowAsync("Anchor e2e - Personal - Microsoft Edge", "MSEdge.UserData.Profile1");

        // 1. The extension is woken in the school profile, then Edge is paused as
        //    not connected: both windows go down, and the overlay says why.
        await run.WaitForAsync(
            s => s.SchoolProfile?.LastOpened is { Profile: "Default" }, Grace + TimeSpan.FromSeconds(10),
            "the agent to wake the extension in the school profile");
        await run.WaitForAsync(
            s => s.ExtensionGuard is { State: "Paused", PauseReason: "NotConnected" } && s.Overlay == "NotConnectedPaused",
            Grace + TimeSpan.FromSeconds(10), "Edge to be paused as not connected, with its overlay up");
        foreach (var (window, what) in new[] { (school, "school profile's"), (personal, "personal profile's") })
            Assert.True(
                await BrowserPauseRun.WaitForIconicAsync(window.Handle, expected: true, TimeSpan.FromSeconds(5)),
                $"Edge was paused as not connected, but the {what} window was not minimized.");

        // Bringing "Edge" forward brings the main window of a process with the
        // browser's name forward (AppIdentifier). Here that is one of these two, or
        // this spec could not tell.
        using (var self = Process.GetCurrentProcess())
        {
            var mains = Process.GetProcessesByName(self.ProcessName)
                .Select(p => { using (p) return p.MainWindowHandle; })
                .Where(h => h != IntPtr.Zero)
                .ToArray();
            Assert.True(
                mains.Length == 1 && (mains[0] == school.Handle || mains[0] == personal.Handle),
                $"Bringing the browser forward would pick {string.Join(", ", mains.Select(h => $"0x{h:X}"))}, " +
                $"not one of the spec's Edge windows (0x{school.Handle:X}, 0x{personal.Handle:X}).");
        }

        // 2. The student clicks the overlay's button: the repair window opens, in
        //    the profile the extension was woken in...
        using var sampler = new ForegroundSampler(run.Agent.Pid);
        sampler.Name(school.Handle, "school profile's window");
        sampler.Name(personal.Handle, "personal profile's window");
        sampler.Mark("clicking the overlay's button");
        Assert.True(await run.Agent.ClickOverlayRepairAsync(), "The overlay was not up to click.");
        var repairing = await run.WaitForAsync(
            s => s.ExtensionGuard is { State: "Repairing", PauseReason: "NotConnected" }, TimeSpan.FromSeconds(5),
            "the overlay's button to open Edge for the repair window");
        Assert.Equal("Default", repairing!.SchoolProfile?.LastOpened?.Profile);
        Assert.StartsWith("https://", repairing.SchoolProfile!.LastOpened!.Url);

        // ...and no Edge window is brought back by the overlay, not even for a
        // moment: the spec's Edge is never started, so only the overlay could.
        await Task.Delay(TimeSpan.FromSeconds(3));
        foreach (var (window, what) in new[] { (school, "school profile's"), (personal, "personal profile's") })
            Assert.False(
                sampler.SawSince("clicking the overlay's button", window.Handle, "restored")
                || !WindowCapture.IsIconic(window.Handle),
                $"The overlay's button brought the {what} window forward itself. It must leave that to the " +
                $"woken profile's Edge: with several profiles open, bringing Edge forward can pick another " +
                $"profile's window (#597). Foreground: {sampler.Describe()}");
    }

    [Fact]
    public async Task A_device_without_the_extension_in_any_profile_is_left_alone()
    {
        // The other side of the same rule: Edge has the school profile (Default) and
        // a personal one (Profile 1), and the extension in neither: never installed,
        // or not yet. Its link never comes up. Nothing filters Edge on this device,
        // so pausing it, waking an extension that isn't there, or telling the
        // teacher would only stop a student who did nothing: both guards stay off,
        // as before #556.
        var api = new BackendClient(_backend.Url);
        await using var run = await SchoolProfileRun.StartAsync(
            _backend.Url, api, signedIn: true, schoolProfileHasExtension: false, extensionConnects: false,
            personalProfileHasExtension: false, grace: Grace);

        // A window of each profile on screen. The agent reads the school profile as
        // one without the extension: where the extension is installed in another
        // profile, Edge is paused for that, and the teacher told (#536, #519).
        var school = await run.OpenWindowAsync("Anchor e2e - School - Microsoft Edge", "MSEdge");
        var personal = await run.OpenWindowAsync("Anchor e2e - Personal - Microsoft Edge", "MSEdge.UserData.Profile1");
        await run.WaitForAsync(
            s => s.SchoolProfile is { Status: "Missing", WithoutExtension: "Default" }
                 && s.ExtensionGuard?.WitnessConnected == false,
            TimeSpan.FromSeconds(10), "the agent to read the school profile as one without the extension");

        // Two grace periods (where it is installed: the wake, then the pause) and
        // the report time go by. The guard stays Off throughout, nothing is opened
        // to wake it, no window is paused and no overlay comes up.
        var states = new HashSet<string>();
        var overlays = new HashSet<string>();
        var schoolPaused = false;
        var personalPaused = false;
        StatusSnapshot? last = null;
        var until = DateTime.UtcNow + Grace + Grace + SchoolProfileRun.Report + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < until)
        {
            last = await run.Agent.TryGetStatusAsync() ?? last;
            if (last?.ExtensionGuard?.State is { } state) states.Add(state);
            if (last?.Overlay is { } overlay) overlays.Add(overlay);
            schoolPaused |= WindowCapture.IsIconic(school.Handle);
            personalPaused |= WindowCapture.IsIconic(personal.Handle);
            await Task.Delay(250);
        }
        Assert.True(
            states.SetEquals(["Off"]),
            $"The guard left Off on a device without the extension (states seen: {string.Join(", ", states)}).");
        Assert.Null(last!.SchoolProfile?.LastOpened);
        Assert.Empty(overlays);
        Assert.False(schoolPaused, "The school profile's window was paused on a device without the extension.");
        Assert.False(personalPaused, "The personal profile's window was paused on a device without the extension.");

        // And the teacher heard nothing.
        foreach (var kind in new[]
                 {
                     "extension_not_connected", "extension_disabled", "outside_school_profile",
                     "school_profile_without_extension", "school_profile_missing",
                 })
            Assert.Equal(0, await TamperReports.CountAsync(api, run.SessionId, kind));
    }

    /// <summary>
    /// A profile's <c>Secure Preferences</c> with the extension installed and on,
    /// as Edge writes it.
    /// </summary>
    private static void WriteInstalledExtension(string userData, string profile)
    {
        var folder = Path.Combine(userData, profile);
        Directory.CreateDirectory(folder);
        var preferences = new JsonObject
        {
            ["extensions"] = new JsonObject
            {
                ["settings"] = new JsonObject
                {
                    [StoreExtensionId] = new JsonObject
                    {
                        ["location"] = 1,
                        ["path"] = $"{StoreExtensionId}\\0.6.0_0",
                        ["disable_reasons"] = new JsonArray(),
                    },
                },
            },
        };
        File.WriteAllText(Path.Combine(folder, "Secure Preferences"), preferences.ToJsonString());
    }

    private static async Task WaitForGuardAsync(
        AgentProcess agent, Func<ExtensionGuardSnapshot, bool> predicate, TimeSpan timeout, string what)
    {
        var last = await agent.WaitForAsync(s => s.ExtensionGuard is { } g && predicate(g), timeout);
        Assert.True(
            last?.ExtensionGuard is { } guard && predicate(guard),
            $"Timed out after {timeout.TotalSeconds:N0}s waiting for {what} (guard: {last?.ExtensionGuard}).");
    }
}
