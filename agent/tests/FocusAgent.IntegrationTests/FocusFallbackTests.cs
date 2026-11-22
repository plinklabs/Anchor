using System.Runtime.Versioning;
using System.Text.Json;
using Xunit.Abstractions;

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
/// The overlay lists the allowed apps, and picking one brings it forward. When
/// that app is not responding, the agent must not wait for it either (#453): the
/// spec picks the suspended Notepad on the overlay (<c>POST /overlay-app</c>, the
/// same click on the agent's UI thread) and checks that the next off-list app is
/// still blocked, and that Notepad comes back once it runs again.
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

    // How often the taskbar spec moves the foreground from Notepad to the taskbar
    // before it gives up on Windows raising the foreground event for the click.
    private const int TaskbarClickAttempts = 3;

    private readonly BackendFixture _backend;
    private readonly ITestOutputHelper _output;

    public FocusFallbackTests(BackendFixture backend, ITestOutputHelper output)
    {
        _backend = backend;
        _output = output;
    }

    [Fact]
    public async Task AfterTheTaskbarHadTheFocus_BlockingAnApp_BringsBackTheStudentsAllowedApp()
    {
        await RunAsync(async (api, sessionId, notepad, offList) =>
        {
            // The student works in Notepad, then clicks the taskbar. Explorer is
            // allowed, so the agent lets it be and reports it.
            var taskbar = WindowCapture.FindTaskbar();
            Assert.True(taskbar != IntPtr.Zero, "No taskbar (Shell_TrayWnd) on this desktop.");
            using var sampler = new ForegroundSampler(agentPid: 0);
            sampler.Name(notepad, "Notepad");
            sampler.Name(taskbar, "taskbar");
            sampler.Name(offList, "off-list app");
            var clicks = await ClickTheTaskbarAsync(notepad, taskbar, sampler);
            _output.WriteLine($"The taskbar click raised its foreground event on attempt {clicks}. Foreground: {sampler.Describe()}");
            if (!await LatestReportNamesAsync(api, sessionId, "explorer", TimeSpan.FromSeconds(10)))
            {
                var events = await api.GetSessionEventsAsync(sessionId);
                Assert.Fail(
                    "Windows raised the foreground event for the taskbar, but the agent did not report the taskbar " +
                    "(explorer) as the latest foreground change within 10s: it missed a foreground change the " +
                    $"student made. Events: {string.Join("; ", events.Select(e => $"{e.Kind} {e.PayloadJson}"))}. " +
                    $"Foreground: {sampler.Describe()}");
            }

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
                $"(off-list app minimized: {WindowCapture.IsIconic(offList)}, Notepad: {WindowCapture.DescribePlacement(notepad)}, " +
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
                $"(off-list app minimized: {WindowCapture.IsIconic(offList)}, Notepad: {WindowCapture.DescribePlacement(notepad)}, foreground: " +
                $"{WindowCapture.GetProcessName(WindowCapture.ForegroundWindow())} " +
                $"[{WindowCapture.GetClassName(WindowCapture.ForegroundWindow())}]). " +
                "Whatever they type goes to the minimized off-list app (#438).");
        });
    }

    /// <summary>
    /// #563: an off-list app restored from another thread, as the spec's
    /// <see cref="WindowCapture.RestoreWindow"/> does. Windows carries that out in
    /// steps it sends to the window's thread: the window is activated, which the
    /// agent blocks, and the window's thread can carry the block's minimize out
    /// before the step that moves the window back on screen, which then puts it
    /// there while it counts as minimized. The block on the restore's second event
    /// (MINIMIZEEND) changes nothing. On CI that happened once by chance; the spec
    /// makes it happen: it restores the window without activating it, and the
    /// window activates itself right before the move and holds the restore there
    /// until the agent's minimize has landed
    /// (<see cref="OffListWindow.MinimizeInTheMiddleOfTheNextRestore"/>). The window
    /// is shown without being activated, so that activating it is a foreground
    /// change the agent hears of, whichever spec ran before.
    /// </summary>
    [Fact]
    public async Task ABlockThatLandsInTheMiddleOfARestore_StillTakesTheAppDown_AndGivesTheKeyboardBack()
    {
        await RunWithOffListWindowAsync(async (_, _, notepad, offList) =>
        {
            // The student works in Notepad...
            Assert.True(
                WindowCapture.BringToForeground(notepad),
                "Notepad could not be brought to the foreground " +
                $"(foreground: {WindowCapture.GetClassName(WindowCapture.ForegroundWindow())}).");

            // ...then another thread brings up the off-list app, and the agent's block
            // lands in the middle of that.
            offList.MinimizeInTheMiddleOfTheNextRestore();
            WindowCapture.RestoreWithoutActivating(offList.Handle);
            Assert.True(
                offList.ActivatedInTheMiddleOfARestore,
                "The off-list app could not take the foreground in the middle of its restore: Windows no longer let the " +
                "spec's process give a window the foreground, so the agent had no event to block it on before the " +
                "restore was done. Someone using the desktop takes that away; run the spec again without touching it " +
                $"(foreground: {WindowCapture.GetProcessName(WindowCapture.ForegroundWindow())}).");
            Assert.True(
                offList.MinimizedInTheMiddleOfARestore,
                $"The agent did not block the off-list app while it was restored: no minimize landed in the " +
                $"{OffListWindow.HoldARestoreFor.TotalSeconds:N0} s the spec held the restore " +
                $"({WindowCapture.DescribePlacement(offList.Handle)}, foreground: " +
                $"{WindowCapture.GetProcessName(WindowCapture.ForegroundWindow())}).");
            Assert.True(
                offList.CameBackOnScreenWhileMinimized,
                "The rest of the restore did not put the off-list app back on screen while it counted as minimized; " +
                "the spec does not reproduce #563.");

            Assert.True(
                await WaitUntilAsync(
                    () => offList.HasGoneDown && WindowCapture.ForegroundWindow() == notepad, TimeSpan.FromSeconds(5)),
                "A block whose minimize landed in the middle of a restore from another thread left the off-list app " +
                $"up (off-list app: {WindowCapture.DescribePlacement(offList.Handle)}, gone down: {offList.HasGoneDown}, " +
                $"Notepad: {WindowCapture.DescribePlacement(notepad)}, foreground: " +
                $"{WindowCapture.GetProcessName(WindowCapture.ForegroundWindow())} " +
                $"[{WindowCapture.GetClassName(WindowCapture.ForegroundWindow())}]) (#563).");
        }, activateOffList: false);
    }

    /// <summary>
    /// #580: the other order of #563. The off-list app takes the foreground in the
    /// middle of a restore from another thread before the restore starts to move it
    /// back on screen, as a <c>SW_RESTORE</c> activates the window first, and the
    /// agent's block lands there
    /// (<see cref="OffListWindow.MinimizeInTheMiddleOfTheNextRestore"/> with
    /// <c>beforeTheMove</c>). The app stays down, but Windows took its spot, parked
    /// off the screen, as the place it goes back to: restored once the session is
    /// over, it would come back off every monitor, out of the student's sight. The
    /// agent saw the app on screen when the session-start sweep took it down, and
    /// must give it back that place.
    /// </summary>
    [Fact]
    public async Task ABlockThatLandsBeforeARestoreMovesTheApp_LeavesItComingBackWhereItWas()
    {
        await RunWithOffListWindowAsync(async (_, _, notepad, offList) =>
        {
            var restorePlace = WindowCapture.RestorePlace(offList.Handle);
            Assert.True(
                WindowCapture.IsOnPrimaryMonitor(offList.Handle),
                $"The off-list app does not go back to the primary monitor before the spec starts " +
                $"({WindowCapture.DescribePlacement(offList.Handle)}).");

            // The student works in Notepad...
            Assert.True(
                WindowCapture.BringToForeground(notepad),
                "Notepad could not be brought to the foreground " +
                $"(foreground: {WindowCapture.GetClassName(WindowCapture.ForegroundWindow())}).");

            // ...then another thread brings up the off-list app, and the agent's block
            // lands before the restore moves it.
            offList.MinimizeInTheMiddleOfTheNextRestore(beforeTheMove: true);
            WindowCapture.RestoreWithoutActivating(offList.Handle);
            Assert.True(
                offList.ActivatedInTheMiddleOfARestore,
                "The off-list app could not take the foreground in the middle of its restore: Windows no longer let the " +
                "spec's process give a window the foreground, so the agent had no event to block it on before the " +
                "restore was done. Someone using the desktop takes that away; run the spec again without touching it " +
                $"(foreground: {WindowCapture.GetProcessName(WindowCapture.ForegroundWindow())}).");
            Assert.True(
                offList.MinimizedInTheMiddleOfARestore,
                $"The agent did not block the off-list app while it was restored: no minimize landed in the " +
                $"{OffListWindow.HoldARestoreFor.TotalSeconds:N0} s the spec held the restore " +
                $"({WindowCapture.DescribePlacement(offList.Handle)}, foreground: " +
                $"{WindowCapture.GetProcessName(WindowCapture.ForegroundWindow())}).");
            Assert.True(
                offList.HasGoneDown && !offList.CameBackOnScreenWhileMinimized,
                "The rest of the restore brought the off-list app up; the spec does not reproduce #580 " +
                $"({WindowCapture.DescribePlacement(offList.Handle)}).");

            Assert.True(
                await WaitUntilAsync(
                    () => WindowCapture.RestorePlace(offList.Handle) == restorePlace, TimeSpan.FromSeconds(5)),
                "A block whose minimize landed before a restore from another thread moved the off-list app left it " +
                $"coming back somewhere else ({WindowCapture.DescribePlacement(offList.Handle)}, goes back to " +
                $"{WindowCapture.RestorePlace(offList.Handle)}, was {restorePlace}) (#580).");
            Assert.True(
                offList.HasGoneDown && WindowCapture.IsOnPrimaryMonitor(offList.Handle),
                $"The off-list app is not down, going back to the primary monitor ({WindowCapture.DescribePlacement(offList.Handle)}).");
            Assert.True(
                await WaitUntilAsync(() => WindowCapture.ForegroundWindow() == notepad, TimeSpan.FromSeconds(5)),
                "Notepad did not get the keyboard back " +
                $"(foreground: {WindowCapture.GetProcessName(WindowCapture.ForegroundWindow())} " +
                $"[{WindowCapture.GetClassName(WindowCapture.ForegroundWindow())}]).");
        }, activateOffList: false);
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
                $"(Notepad: {WindowCapture.DescribePlacement(notepad)}). Latest foreground reports, newest first: " +
                $"{await DescribeLatestReportsAsync(api, sessionId)}.");
        });
    }

    [Fact]
    public async Task WhenTheStudentPicksAnAllowedAppThatIsNotRespondingOnTheOverlay_TheAgentStillBlocksOffListApps()
    {
        var api = new BackendClient(_backend.Url);

        // Notepad is open but put away before the session, so the student has not
        // used an allowed app yet: a block shows the overlay, which lists Notepad.
        var notepad = await TestNotepad.OpenOnPrimaryMonitorAsync();
        try
        {
            WindowCapture.MinimizeWithoutActivating(notepad);
            Assert.True(await WaitUntilAsync(() => WindowCapture.IsIconic(notepad), TimeSpan.FromSeconds(3)),
                "Notepad could not be minimized.");
            // Opened before the session, and minimized: a window that comes up can
            // move the foreground on, to an allowed app of the developer's that
            // would then be the fallback, and no overlay would come up. For the same
            // reason the spec leaves the foreground alone until the session runs.
            using var offList = await OffListWindow.OpenAsync(
                "Anchor e2e off-list app", TimeSpan.FromSeconds(10), minimized: true);
            using var nextOffList = await OffListWindow.OpenAsync(
                "Anchor e2e next off-list app", TimeSpan.FromSeconds(10), minimized: true);

            await using var agent = AgentProcess.Launch(_backend.Url, TestConfig.StudentOid, autoJoin: true);
            await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));
            using var sampler = new ForegroundSampler(agent.Pid);
            sampler.Name(offList.Handle, "off-list app");
            sampler.Name(nextOffList.Handle, "next off-list app");
            sampler.Name(notepad, "Notepad");

            sampler.Mark("starting the session");
            var classId = await api.FindClassIdAsync();
            var appBundle = await api.FindBundleIdAsync(TestConfig.AppBundleName);
            var session = await api.StartSessionAsync(classId, new[] { appBundle });
            try
            {
                var joined = await agent.WaitForAsync(
                    s => s.JoinedSessionId == session.Id && AllowsApp(s) && s.StartupSweep is not null,
                    TimeSpan.FromSeconds(10));
                Assert.True(
                    joined?.JoinedSessionId == session.Id && AllowsApp(joined) && joined.StartupSweep is not null,
                    $"Agent did not join and sweep with '{TestConfig.AppProcessName}' allowed within 10s " +
                    $"(joinedSessionId: {joined?.JoinedSessionId?.ToString() ?? "<none>"}).");

                // The student brings up the off-list app: a click on its taskbar
                // button first activates it while it is minimized. With nothing to
                // fall back to, the agent blocks it and its overlay takes the keyboard
                // (#441).
                sampler.Mark("opening the off-list app");
                var activated = false;
                for (var attempt = 0; attempt < 3 && !activated; attempt++)
                {
                    activated = WindowCapture.ActivateOnce(offList.Handle);
                    if (!activated) await Task.Delay(200);
                }
                var ownInput = WindowCapture.LastInputTick();
                Assert.True(activated, "Windows refused to give the off-list app the foreground.");
                var overlay = IntPtr.Zero;
                string? showing = null;
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                while (DateTime.UtcNow < deadline)
                {
                    overlay = WindowCapture.FindFullScreenWindow(agent.Pid);
                    showing = (await agent.TryGetStatusAsync())?.Overlay;
                    if (offList.HasGoneDown && showing == "Blocked" && overlay != IntPtr.Zero &&
                        WindowCapture.ForegroundWindow() == overlay)
                        break;
                    await Task.Delay(100);
                }
                Assert.True(
                    showing == "Blocked" && overlay != IntPtr.Zero && offList.HasGoneDown,
                    $"The agent did not block the off-list app with its overlay (overlay: '{showing ?? "<hidden>"}', " +
                    $"off-list app down: {offList.HasGoneDown}); with no allowed window to fall back to it should " +
                    "have (#441). An allowed app that had the foreground since the session started is a window to " +
                    $"fall back to instead.{Interference(ownInput)} Foreground: {sampler.Describe()}");
                Assert.True(
                    WindowCapture.ForegroundWindow() == overlay,
                    $"The overlay did not get the keyboard, as the student's click on it would give it (#441); the " +
                    $"foreground is {DescribeForeground()}.{Interference(ownInput)} Foreground: {sampler.Describe()}");

                // Notepad stops responding, and the student picks it on the overlay.
                using var notResponding = SuspendedProcess.Suspend(WindowCapture.ProcessIdOf(notepad));
                Assert.False(SuspendedProcess.IsResponding(notepad), "Notepad still responds with its process suspended.");
                sampler.Mark("picking Notepad on the overlay");
                Assert.True(
                    await agent.ClickOverlayAppAsync(TestConfig.AppProcessName),
                    "The overlay was not up to pick Notepad on.");
                // The click runs on the agent's UI thread. Restoring Notepad there
                // waited until Notepad responded: the overlay stayed up, and no
                // foreground change was judged meanwhile (#453).
                var overlayGone = await WaitForStatusAsync(agent, s => s.Overlay is null, TimeSpan.FromSeconds(1));

                // Then they open another off-list app, which the agent must block as
                // always.
                sampler.Mark("opening the next off-list app");
                nextOffList.Restore();
                var nextDown = await WaitUntilAsync(() => nextOffList.HasGoneDown, TimeSpan.FromSeconds(2));
                Assert.False(
                    SuspendedProcess.IsResponding(notepad),
                    "Notepad started responding again before the spec could check the agent.");
                Assert.True(
                    nextDown,
                    "While the allowed app the student picked on the overlay was not responding, the agent did not " +
                    "block the next off-list app: it was waiting to bring that app forward (#453). " +
                    $"Foreground: {sampler.Describe()}");
                Assert.True(
                    overlayGone,
                    "The overlay stayed up after the student picked Notepad on it: the click did not get through, " +
                    $"or waited for Notepad (#453). Foreground: {sampler.Describe()}");

                // Notepad comes back as soon as it responds.
                notResponding.Dispose();
                Assert.True(
                    await WaitUntilAsync(
                        () => !WindowCapture.IsIconic(notepad) && WindowCapture.GetWindowScreenRect(notepad).Left > MinimizedPosition,
                        TimeSpan.FromSeconds(3)),
                    "The allowed app the student picked on the overlay did not come back once it responded again " +
                    $"(Notepad: {WindowCapture.DescribePlacement(notepad)}). Foreground: {sampler.Describe()}");
            }
            finally
            {
                try { await api.EndSessionAsync(session.Id); } catch { /* best-effort */ }
            }
        }
        finally
        {
            TestNotepad.Kill();
        }
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
    private Task RunAsync(Func<BackendClient, Guid, IntPtr, IntPtr, Task> scenario) =>
        RunWithOffListWindowAsync((api, sessionId, notepad, offList) => scenario(api, sessionId, notepad, offList.Handle));

    /// <summary>
    /// <see cref="RunAsync"/>, for a scenario that needs the off-list window itself
    /// rather than its handle. An off-list window shown without
    /// <paramref name="activateOffList"/> never takes the foreground, nor is it the
    /// active window of its own thread, which it stays when Windows does not let it
    /// take the foreground: minimized from the agent by the session-start sweep, it
    /// would then be made the foreground window, without a foreground event, as it
    /// is restored.
    /// </summary>
    private async Task RunWithOffListWindowAsync(
        Func<BackendClient, Guid, IntPtr, OffListWindow, Task> scenario, bool activateOffList = true)
    {
        var api = new BackendClient(_backend.Url);

        // A fresh window must be up before the session starts.
        var notepad = await TestNotepad.OpenOnPrimaryMonitorAsync();
        try
        {
            using var offList = await OffListWindow.OpenAsync(
                "Anchor e2e off-list app", TimeSpan.FromSeconds(10), activate: activateOffList);

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
                Assert.False(
                    WindowCapture.IsIconic(notepad),
                    $"The allowed Notepad was minimized at session start ({WindowCapture.DescribePlacement(notepad)}). " +
                    TestNotepad.TakenDownFromOutside);

                // The student works in Notepad: bring it back from minimized without
                // activating it, so the agent judges it while the foreground stays put.
                WindowCapture.MinimizeWithoutActivating(notepad);
                Assert.True(await WaitUntilAsync(() => WindowCapture.IsIconic(notepad), TimeSpan.FromSeconds(3)),
                    "Notepad could not be minimized.");
                WindowCapture.RestoreWithoutActivating(notepad);
                Assert.True(await WaitUntilAsync(() => !WindowCapture.IsIconic(notepad), TimeSpan.FromSeconds(3)),
                    "Notepad could not be restored.");
                await WaitForLatestReportAsync(api, session.Id, TestConfig.AppProcessName, "Notepad");

                await scenario(api, session.Id, notepad, offList);
            }
            finally
            {
                try { await api.EndSessionAsync(session.Id); } catch { /* best-effort */ }
            }
        }
        finally
        {
            TestNotepad.Kill();
        }
    }

    /// <summary>
    /// The student works in <paramref name="notepad"/>, then clicks the
    /// <paramref name="taskbar"/>: each step is a foreground change that Windows
    /// raises <c>EVENT_SYSTEM_FOREGROUND</c> for, the event the agent judges
    /// windows on. Returns on which attempt the click raised it; fails the spec
    /// when it never does.
    ///
    /// <see cref="WindowCapture.BringToForeground"/> returning true does not show
    /// that (#554). It checks <c>GetForegroundWindow</c>, and on the CI runner
    /// Windows has made a window the foreground window without raising the event.
    /// In one run the #560 spec's own hook saw Notepad come forward from the
    /// agent's window, then no event at all for the window it brought forward
    /// right after, which the agent's sweep then found to have the foreground. In
    /// the same run this spec's two moves (Notepad, then the taskbar) both
    /// returned true, and the agent got no event for the taskbar. So the spec
    /// watches for the event on a hook of its own (<paramref name="sampler"/>),
    /// gives Notepad's move time to finish before the click, and tries both moves
    /// again when the click raised nothing.
    /// </summary>
    private static async Task<int> ClickTheTaskbarAsync(IntPtr notepad, IntPtr taskbar, ForegroundSampler sampler)
    {
        for (var attempt = 1; attempt <= TaskbarClickAttempts; attempt++)
        {
            var working = $"working in Notepad ({attempt})";
            sampler.Mark(working);
            Assert.True(
                WindowCapture.BringToForeground(notepad),
                $"Notepad could not be brought to the foreground. Foreground: {sampler.Describe()}");
            // Notepad raises no event when it had the foreground already.
            await WaitUntilAsync(() => sampler.SawSince(working, notepad, "foreground"), TimeSpan.FromMilliseconds(500));

            var clicking = $"clicking the taskbar ({attempt})";
            sampler.Mark(clicking);
            Assert.True(
                WindowCapture.BringToForeground(taskbar),
                $"The taskbar could not be brought to the foreground. Foreground: {sampler.Describe()}");
            if (await WaitUntilAsync(() => sampler.SawSince(clicking, taskbar, "foreground"), TimeSpan.FromSeconds(1)))
                return attempt;
        }
        Assert.Fail(
            $"Windows raised no foreground event for the taskbar in {TaskbarClickAttempts} clicks, although each time " +
            "GetForegroundWindow named it: there was no foreground change for the agent to judge or report (#554). " +
            $"Foreground: {sampler.Describe()}");
        return TaskbarClickAttempts;
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

    /// <summary>Whether the agent's /status meets <paramref name="predicate"/> within <paramref name="timeout"/>.</summary>
    private static async Task<bool> WaitForStatusAsync(AgentProcess agent, Func<StatusSnapshot, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            if (await agent.TryGetStatusAsync() is { } status && predicate(status))
                return true;
            await Task.Delay(50);
        }
        while (DateTime.UtcNow < deadline);
        return false;
    }

    /// <summary>
    /// Says so in a failure message when someone used the desktop since the spec's
    /// last key (<paramref name="ownInput"/>): a click or key of theirs moves the
    /// foreground without the agent having a part in it.
    /// </summary>
    private static string Interference(uint ownInput) =>
        WindowCapture.LastInputTick() == ownInput
            ? ""
            : " The desktop had input during this step that the spec did not send: someone using it moves the " +
              "foreground without the agent. Run the spec again without touching it.";

    private static string DescribeForeground()
    {
        var foreground = WindowCapture.ForegroundWindow();
        return $"{WindowCapture.GetProcessName(foreground)} [{WindowCapture.GetClassName(foreground)}]";
    }

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
}
