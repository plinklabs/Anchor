using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Asserting e2e for #437: where the agent sends the student after it blocks an
/// app. Explorer is allowed in every session, so a click on the taskbar used to
/// make the taskbar, its thumbnail flyout or the desktop the window the agent
/// handed focus back to. Re-showing the flyout's closing window that way crashed
/// explorer. The student must get their own allowed app back instead, with the
/// keyboard (#438), and the agent must never put a window that went away back on
/// screen.
///
/// The real agent judges real windows on the real foreground hook: an allowed
/// Notepad (the dev "Notepad (dev)" bundle), the real taskbar, and an off-list
/// window the test process shows itself (<see cref="OffListWindow"/>). Restoring
/// a minimized window raises MINIMIZEEND whether or not Windows hands it the
/// foreground, which is the lever both specs pull; the taskbar is brought forward
/// the way the agent's enforcer brings a window forward. Observes through window
/// state. Real foreground events on a headless desktop are the flaky part, so
/// this runs in the non-blocking Visual lane, like <see cref="ReMinimizeOnRestoreTests"/>.
///
/// A minimized allowed app is restored to fall back to. When that app is not
/// responding, the agent must not wait for it (#451): the spec suspends Notepad's
/// process (<see cref="SuspendedProcess"/>) and checks that off-list apps are
/// still blocked meanwhile, and that Notepad comes back once it runs again.
///
/// With no allowed window to fall back to, the agent shows its focus overlay,
/// which must get the keyboard (#441). That spec types after the block and counts
/// what reaches the off-list window. It only ever types while the foreground
/// window is the off-list window or the agent's, never into another app.
///
/// Note: like every spec that starts a session, this minimizes off-list windows
/// on the live desktop while it runs.
/// </summary>
[Trait("Category", "Visual")]
[Collection(AgentE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class FocusFallbackTests
{
    private const string ForegroundChange = "ForegroundChange";
    // Where Windows parks a minimized top-level window.
    private const int MinimizedPosition = -32000;

    private readonly BackendFixture _backend;
    public FocusFallbackTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task AfterTheTaskbarHadTheFocus_BlockingAnApp_BringsBackTheStudentsAllowedApp()
    {
        await RunAsync(async (api, sessionId, notepad, offList) =>
        {
            // The student clicks the taskbar. Explorer is allowed, so the agent lets
            // it be and reports it.
            var taskbar = WindowCapture.FindTaskbar();
            Assert.True(taskbar != IntPtr.Zero, "No taskbar (Shell_TrayWnd) on this desktop.");
            Assert.True(
                WindowCapture.BringToForeground(taskbar),
                "The taskbar could not be brought to the foreground " +
                $"(foreground: {WindowCapture.GetClassName(WindowCapture.ForegroundWindow())}).");
            await WaitForLatestReportAsync(api, sessionId, "explorer", "the taskbar");

            // Notepad is minimized meanwhile, so whether the agent hands focus back
            // to it shows: it comes back up.
            WindowCapture.MinimizeWithoutActivating(notepad);
            Assert.True(await WaitUntilAsync(() => WindowCapture.IsIconic(notepad), TimeSpan.FromSeconds(3)),
                "Notepad could not be minimized.");

            // Then they open an off-list app, which the agent blocks.
            WindowCapture.RestoreWindow(offList);
            Assert.True(
                await WaitUntilAsync(
                    () => WindowCapture.IsIconic(offList) && !WindowCapture.IsIconic(notepad), TimeSpan.FromSeconds(5)),
                "After blocking the off-list app the agent did not bring back the student's allowed app " +
                $"(off-list app minimized: {WindowCapture.IsIconic(offList)}, Notepad minimized: {WindowCapture.IsIconic(notepad)}, " +
                $"foreground: {WindowCapture.GetClassName(WindowCapture.ForegroundWindow())}). " +
                "The taskbar is no window to hand focus back to (#437).");
        });
    }

    [Fact]
    public async Task BlockingAnApp_GivesTheKeyboardBackToTheStudentsAllowedApp()
    {
        await RunAsync(async (_, _, notepad, offList) =>
        {
            // The student works in Notepad...
            Assert.True(
                WindowCapture.BringToForeground(notepad),
                "Notepad could not be brought to the foreground " +
                $"(foreground: {WindowCapture.GetClassName(WindowCapture.ForegroundWindow())}).");

            // ...then brings up the off-list app, which the agent blocks.
            WindowCapture.RestoreWindow(offList);

            // The foreground window gets what the student types. Minimized from the
            // agent, the blocked window stayed the foreground window and kept the
            // keyboard; minimized by its own thread, it hands the foreground on (#438).
            Assert.True(
                await WaitUntilAsync(
                    () => WindowCapture.IsIconic(offList) && WindowCapture.ForegroundWindow() == notepad,
                    TimeSpan.FromSeconds(5)),
                "After blocking the off-list app the student's allowed app did not get the keyboard back " +
                $"(off-list app minimized: {WindowCapture.IsIconic(offList)}, foreground: " +
                $"{WindowCapture.GetProcessName(WindowCapture.ForegroundWindow())} " +
                $"[{WindowCapture.GetClassName(WindowCapture.ForegroundWindow())}]). " +
                "Whatever they type goes to the minimized off-list app (#438).");
        });
    }

    [Fact]
    public async Task BlockingAnApp_NeverPutsAHiddenAllowedWindowBackOnScreen()
    {
        await RunAsync(async (_, _, notepad, offList) =>
        {
            // The window the agent would hand focus back to goes away, as the
            // taskbar's thumbnail flyout does when it closes.
            WindowCapture.HideWindow(notepad);
            Assert.True(await WaitUntilAsync(() => !WindowCapture.IsVisible(notepad), TimeSpan.FromSeconds(3)),
                "Notepad could not be hidden.");

            WindowCapture.RestoreWindow(offList);
            Assert.True(
                await WaitUntilAsync(() => WindowCapture.IsIconic(offList), TimeSpan.FromSeconds(5)),
                "The agent did not block the off-list app.");

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                Assert.False(
                    WindowCapture.IsVisible(notepad),
                    "Blocking the off-list app put the hidden Notepad back on screen: the agent must never " +
                    "show a hidden window to hand focus back to it (#437).");
                await Task.Delay(100);
            }
        });
    }

    [Fact]
    public async Task WhileTheStudentsMinimizedAllowedAppIsNotResponding_TheAgentStillBlocksOffListApps()
    {
        await RunAsync(async (api, sessionId, notepad, offList) =>
        {
            // Opened first: a window that comes up can move the foreground on, to an
            // allowed app of the developer's that would then be the fallback instead
            // of Notepad.
            using var nextOffList = await OffListWindow.OpenAsync(
                "Anchor e2e next off-list app", TimeSpan.FromSeconds(10), minimized: true);

            // The student works in Notepad, then puts it away, so the agent restores
            // it to fall back to when it blocks an app. Then Notepad stops responding.
            WindowCapture.MinimizeWithoutActivating(notepad);
            Assert.True(await WaitUntilAsync(() => WindowCapture.IsIconic(notepad), TimeSpan.FromSeconds(3)),
                "Notepad could not be minimized.");
            WindowCapture.RestoreWithoutActivating(notepad);
            Assert.True(await WaitUntilAsync(() => !WindowCapture.IsIconic(notepad), TimeSpan.FromSeconds(3)),
                "Notepad could not be restored.");
            await WaitForLatestReportAsync(api, sessionId, TestConfig.AppProcessName, "Notepad");
            WindowCapture.MinimizeWithoutActivating(notepad);
            Assert.True(await WaitUntilAsync(() => WindowCapture.IsIconic(notepad), TimeSpan.FromSeconds(3)),
                "Notepad could not be minimized.");
            using var notResponding = SuspendedProcess.Suspend(WindowCapture.ProcessIdOf(notepad));
            Assert.False(SuspendedProcess.IsResponding(notepad), "Notepad still responds with its process suspended.");

            // They open an off-list app, then another. Restoring Notepad from the
            // agent's UI thread waited until Notepad responded: the first app went
            // down no sooner, and the second was not even judged meanwhile (#451).
            WindowCapture.RestoreWindow(offList);
            var firstDown = await WaitUntilAsync(() => WindowCapture.IsIconic(offList), TimeSpan.FromSeconds(2));
            nextOffList.Restore();
            var nextDown = await WaitUntilAsync(() => nextOffList.HasGoneDown, TimeSpan.FromSeconds(2));
            Assert.False(
                SuspendedProcess.IsResponding(notepad),
                "Notepad started responding again before the spec could check the agent.");
            Assert.True(
                firstDown,
                "While the student's allowed app was not responding, the agent did not block an off-list app: " +
                "it was waiting to restore the allowed app to fall back to (#451).");
            Assert.True(
                nextDown,
                "While the student's allowed app was not responding, the agent did not block the next off-list app " +
                "(#451).");

            // Notepad comes back as soon as it responds.
            notResponding.Dispose();
            Assert.True(
                await WaitUntilAsync(
                    () => !WindowCapture.IsIconic(notepad) && WindowCapture.GetWindowScreenRect(notepad).Left > MinimizedPosition,
                    TimeSpan.FromSeconds(3)),
                "The student's allowed app did not come back once it responded again " +
                $"(Notepad minimized: {WindowCapture.IsIconic(notepad)}). Latest foreground reports, newest first: " +
                $"{await DescribeLatestReportsAsync(api, sessionId)}.");
        });
    }

    [Fact]
    public async Task WithNothingToFallBackTo_TheOverlayGetsTheKeyboard_SoTypingNeverReachesTheBlockedApp()
    {
        var api = new BackendClient(_backend.Url);
        await using var agent = AgentProcess.Launch(_backend.Url, TestConfig.StudentOid, autoJoin: true);
        await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));

        // No bundles, and the student has not used an allowed app yet: there is no
        // window to hand focus back to, so a block shows the overlay.
        var classId = await api.FindClassIdAsync();
        var session = await api.StartSessionAsync(classId);
        try
        {
            var joined = await agent.WaitForAsync(
                s => s.JoinedSessionId == session.Id && s.StartupSweep is not null, TimeSpan.FromSeconds(10));
            Assert.True(
                joined?.JoinedSessionId == session.Id && joined.StartupSweep is not null,
                $"Agent did not join and sweep within 10s (joinedSessionId: {joined?.JoinedSessionId?.ToString() ?? "<none>"}).");

            // The student opens an off-list app before any allowed one.
            using var sampler = new ForegroundSampler(agent.Pid);
            using var offList = await OffListWindow.OpenAsync("Anchor e2e off-list app", TimeSpan.FromSeconds(10));
            sampler.Name(offList.Handle, "off-list app");
            await AssertTheOverlayHasTheKeyboardAsync(agent, offList, sampler, "opening the off-list app");

            // Then they activate it again while it is minimized, the first thing a
            // click on its taskbar button does.
            await AssertTheOverlayHasTheKeyboardAsync(agent, offList, sampler, "activating the minimized off-list app");
        }
        finally
        {
            try { await api.EndSessionAsync(session.Id); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Bring <paramref name="offList"/> to the foreground as the student would, then
    /// type: after the block, the overlay must be on screen with the keyboard, and
    /// not a single letter may reach the off-list app (#441).
    /// </summary>
    private static async Task AssertTheOverlayHasTheKeyboardAsync(
        AgentProcess agent, OffListWindow offList, ForegroundSampler sampler, string step)
    {
        const int lettersTyped = 6;

        sampler.Mark(step);
        var activated = false;
        for (var attempt = 0; attempt < 3 && !activated; attempt++)
        {
            activated = WindowCapture.ActivateOnce(offList.Handle);
            if (!activated) await Task.Delay(200);
        }
        Assert.True(activated, $"Windows refused to give the off-list app the foreground ({step}).");

        // The agent blocks it: the window goes down, and the overlay takes the
        // foreground over from it. Minimized from the agent, the window kept the
        // foreground instead, without the student seeing it.
        await WaitUntilAsync(
            () => WindowCapture.IsIconic(offList.Handle) && WindowCapture.ForegroundWindow() != offList.Handle,
            TimeSpan.FromSeconds(2));
        Assert.True(WindowCapture.IsIconic(offList.Handle), $"The agent did not block the off-list app ({step}).");
        await Task.Delay(300);

        var before = offList.KeystrokesReceived;
        for (var i = 0; i < lettersTyped; i++)
        {
            // Only ever type into the off-list window or the agent's: anything else
            // is an app of the person running the suite.
            var foreground = WindowCapture.ForegroundWindow();
            var pid = WindowCapture.ProcessIdOf(foreground);
            Assert.True(
                pid == agent.Pid || pid == Environment.ProcessId,
                $"After {step} the keyboard went to {WindowCapture.GetProcessName(foreground)} " +
                $"[{WindowCapture.GetClassName(foreground)}] instead of the overlay (#441). Stopped typing. " +
                $"Foreground: {sampler.Describe()}");
            WindowCapture.PressLetter((char)('A' + i));
            await Task.Delay(100);
        }
        await Task.Delay(300);

        var reached = offList.KeystrokesReceived - before;
        Assert.True(
            reached == 0,
            $"{reached} of the {lettersTyped} letters typed after {step} reached the minimized off-list app, " +
            $"which still has the foreground (#441). Foreground: {sampler.Describe()}");

        // And the student sees why: the overlay is on screen, with the keyboard.
        var overlay = WindowCapture.FindFullScreenWindow(agent.Pid);
        Assert.True(
            overlay != IntPtr.Zero,
            $"The overlay was not on screen after {step}. The agent's windows:\n{WindowCapture.DescribeWindows(agent.Pid)}" +
            $"Foreground: {sampler.Describe()}");
        Assert.True(
            WindowCapture.ForegroundWindow() == overlay,
            $"After {step} the overlay is not the foreground window (foreground: " +
            $"{WindowCapture.GetProcessName(WindowCapture.ForegroundWindow())} " +
            $"[{WindowCapture.GetClassName(WindowCapture.ForegroundWindow())}]). Foreground: {sampler.Describe()}");
    }

    /// <summary>
    /// One #437 scenario: a fresh Notepad and an off-list window on screen, a
    /// session joined with Notepad allowed, the off-list window minimized by the
    /// session-start sweep, and Notepad judged by the agent as the student's
    /// current allowed app, without it taking the foreground.
    /// </summary>
    private async Task RunAsync(Func<BackendClient, Guid, IntPtr, IntPtr, Task> scenario)
    {
        var api = new BackendClient(_backend.Url);

        // A fresh window must be up before the session starts (Win11 notepad is a
        // tabbed launcher, so kill leftovers first to guarantee a new window).
        KillNotepad();
        using (Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true })
               ?? throw new InvalidOperationException("Failed to launch notepad."))
        {
        }
        try
        {
            var notepad = await WaitForNotepadWindowAsync(TimeSpan.FromSeconds(10));
            Assert.True(notepad != IntPtr.Zero, "Notepad did not present a top-level window within 10s.");
            using var offList = await OffListWindow.OpenAsync("Anchor e2e off-list app", TimeSpan.FromSeconds(10));

            await using var agent = AgentProcess.Launch(_backend.Url, TestConfig.StudentOid, autoJoin: true);
            await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));

            var classId = await api.FindClassIdAsync();
            var appBundle = await api.FindBundleIdAsync(TestConfig.AppBundleName);
            var session = await api.StartSessionAsync(classId, new[] { appBundle });
            try
            {
                var joined = await agent.WaitForAsync(
                    s => s.JoinedSessionId == session.Id && AllowsApp(s), TimeSpan.FromSeconds(10));
                Assert.True(
                    joined?.JoinedSessionId == session.Id && AllowsApp(joined),
                    $"Agent did not join with '{TestConfig.AppProcessName}' allowed within 10s " +
                    $"(joinedSessionId: {joined?.JoinedSessionId?.ToString() ?? "<none>"}).");
                Assert.True(
                    await WaitUntilAsync(() => WindowCapture.IsIconic(offList.Handle), TimeSpan.FromSeconds(10)),
                    "The session-start sweep did not minimize the off-list window (#104).");
                Assert.False(WindowCapture.IsIconic(notepad), "The allowed Notepad was minimized at session start.");

                // The student works in Notepad: bring it back from minimized without
                // activating it, so the agent judges it while the foreground stays put.
                WindowCapture.MinimizeWithoutActivating(notepad);
                Assert.True(await WaitUntilAsync(() => WindowCapture.IsIconic(notepad), TimeSpan.FromSeconds(3)),
                    "Notepad could not be minimized.");
                WindowCapture.RestoreWithoutActivating(notepad);
                Assert.True(await WaitUntilAsync(() => !WindowCapture.IsIconic(notepad), TimeSpan.FromSeconds(3)),
                    "Notepad could not be restored.");
                await WaitForLatestReportAsync(api, session.Id, TestConfig.AppProcessName, "Notepad");

                await scenario(api, session.Id, notepad, offList.Handle);
            }
            finally
            {
                try { await api.EndSessionAsync(session.Id); } catch { /* best-effort */ }
            }
        }
        finally
        {
            KillNotepad();
        }
    }

    /// <summary>
    /// Wait until the agent's most recent foreground report names
    /// <paramref name="processName"/>: proof it has judged that window. Shared with
    /// <see cref="ExtensionOffFallbackTests"/> (#452).
    /// </summary>
    internal static async Task WaitForLatestReportAsync(BackendClient api, Guid sessionId, string processName, string what)
    {
        if (await LatestReportNamesAsync(api, sessionId, processName, TimeSpan.FromSeconds(10)))
            return;
        var events = await api.GetSessionEventsAsync(sessionId);
        Assert.Fail(
            $"The agent did not report {what} ({processName}) as the latest foreground change within 10s. " +
            $"Events: {string.Join("; ", events.Select(e => $"{e.Kind} {e.PayloadJson}"))}.");
    }

    /// <summary>
    /// Whether the agent's most recent foreground report names
    /// <paramref name="processName"/> within <paramref name="timeout"/>.
    /// </summary>
    internal static async Task<bool> LatestReportNamesAsync(
        BackendClient api, Guid sessionId, string processName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            var events = await api.GetSessionEventsAsync(sessionId);
            // recentEvents is newest first.
            var latest = events.FirstOrDefault(e => e.Kind == ForegroundChange);
            if (latest is not null && Names(latest.PayloadJson, processName))
                return true;
            await Task.Delay(250);
        }
        while (DateTime.UtcNow < deadline);
        return false;
    }

    /// <summary>The agent's latest foreground reports, newest first, for failure messages.</summary>
    private static async Task<string> DescribeLatestReportsAsync(BackendClient api, Guid sessionId)
    {
        var events = await api.GetSessionEventsAsync(sessionId);
        return string.Join("; ", events.Where(e => e.Kind == ForegroundChange).Take(6).Select(e => e.PayloadJson));
    }

    private static bool Names(string payloadJson, string processName)
    {
        using var payload = JsonDocument.Parse(payloadJson);
        return payload.RootElement.TryGetProperty("processName", out var name) &&
               string.Equals(name.GetString(), processName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool AllowsApp(StatusSnapshot status) =>
        (status.AllowedApps ?? Array.Empty<string>()).Contains(TestConfig.AppProcessName);

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }

    private static async Task<IntPtr> WaitForNotepadWindowAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var hwnd = WindowCapture.FindMainWindowByProcessName("notepad");
            if (hwnd != IntPtr.Zero) return hwnd;
            await Task.Delay(200);
        }
        return IntPtr.Zero;
    }

    private static void KillNotepad()
    {
        foreach (var p in Process.GetProcessesByName("notepad"))
        {
            using (p)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            }
        }
    }
}
