using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Asserting e2e for #427: while a joined student's extension is switched off,
/// the agent pauses the browser — its windows come down and stay down — until the
/// extension's witness pipe reconnects, and a reload that reconnects within the
/// grace period pauses nothing.
///
/// Drives the real agent against the real backend over the real witness pipe.
/// Two stand-ins keep it hermetic and off the developer's own browser:
/// <list type="bullet">
/// <item>The extension's side of the pipe is a <see cref="NamedPipeClientStream"/>
/// on a per-run pipe name (<c>Witness__PipeName</c>), connecting exactly like the
/// native host's <c>NamedPipeAgentLink</c>; closing it is what Edge does to the
/// host when the extension is switched off (the host half of that is covered by
/// <c>WitnessHostIntegrationTests</c>).</item>
/// <item>Notepad plays the browser (<c>Witness__BrowserProcessName</c>), allowed
/// through the dev "Notepad (dev)" bundle the way Edge is through the baseline, so
/// the run never minimizes the developer's real Edge windows. The pause logic is
/// the same for any process name; that it targets <c>msedge</c> by default is
/// pinned by the unit tests.</item>
/// </list>
/// Observes through window state (<see cref="WindowCapture.IsIconic"/>) and
/// <c>/status.extensionGuard</c>, and only relies on the agent's own
/// <c>ShowWindow</c> calls — not on foreground hook events, which are the flaky
/// part on a headless desktop (see <see cref="ExtensionOffReMinimizeTests"/> for
/// the restore path, in the Visual lane).
/// </summary>
[Collection(AgentE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class ExtensionOffPausesBrowserTests
{
    private readonly BackendFixture _backend;
    public ExtensionOffPausesBrowserTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task SwitchingTheExtensionOff_PausesTheBrowser_UntilTheWitnessReconnects()
    {
        var api = new BackendClient(_backend.Url);
        await using var run = await BrowserPauseRun.StartAsync(_backend.Url, api);
        var hwnd = run.BrowserWindow;
        var agent = run.Agent;

        // The stand-in browser is allowed, so the session-start sweep left it up.
        Assert.False(WindowCapture.IsIconic(hwnd), "The allowed stand-in browser was minimized at session start.");

        // 1. An extension reload / update / Edge restart: the pipe drops and comes
        //    back within the grace period. Nothing may be paused.
        await run.Extension.DisconnectAsync();
        await run.WaitForGuardAsync(g => !g.WitnessConnected, TimeSpan.FromSeconds(5), "the witness to drop");
        await run.Extension.ConnectAsync();
        await run.WaitForGuardAsync(
            g => g.WitnessConnected && g.State == "Present", TimeSpan.FromSeconds(5), "the witness to reconnect");
        await Task.Delay(BrowserPauseRun.Grace + TimeSpan.FromSeconds(1));
        var afterReload = await agent.TryGetStatusAsync();
        Assert.Equal("Present", afterReload?.ExtensionGuard?.State);
        Assert.False(
            WindowCapture.IsIconic(hwnd),
            "A drop that reconnected within the grace period minimized the browser — a normal reload was punished.");

        // 2. Switched off: once the grace period runs out the browser is paused and
        //    the window already on screen comes down, with no focus change needed.
        await run.Extension.DisconnectAsync();
        await run.WaitForGuardAsync(
            g => g.State == "Paused", BrowserPauseRun.Grace + TimeSpan.FromSeconds(5), "the browser to be paused");
        Assert.True(
            await BrowserPauseRun.WaitForIconicAsync(hwnd, expected: true, TimeSpan.FromSeconds(5)),
            "The browser was paused but its open window was not minimized.");

        // 3. The overlay's "switch the extension back on": the browser opens for the
        //    repair window (so the student can reach edge://extensions)...
        Assert.True(await agent.TurnExtensionBackOnAsync(), "The agent refused to open the paused browser for repair.");
        await run.WaitForGuardAsync(g => g.State == "Repairing", TimeSpan.FromSeconds(3), "the repair window to open");
        WindowCapture.RestoreWindow(hwnd);
        Assert.True(
            await BrowserPauseRun.WaitForIconicAsync(hwnd, expected: false, TimeSpan.FromSeconds(3)),
            "The browser window could not be restored during the repair window.");
        // ...and, when it runs out without the extension, is paused again.
        await run.WaitForGuardAsync(
            g => g.State == "Paused", BrowserPauseRun.Repair + TimeSpan.FromSeconds(5), "the pause to return after repair");
        Assert.True(
            await BrowserPauseRun.WaitForIconicAsync(hwnd, expected: true, TimeSpan.FromSeconds(5)),
            "The repair window ran out but the browser window was not minimized again.");

        // 4. The extension comes back: the pause lifts at once, and the window can
        //    be brought back and stays up.
        await run.Extension.ConnectAsync();
        await run.WaitForGuardAsync(
            g => g.WitnessConnected && g.State == "Present", TimeSpan.FromSeconds(5), "the pause to lift");
        WindowCapture.RestoreWindow(hwnd);
        Assert.True(
            await BrowserPauseRun.WaitForIconicAsync(hwnd, expected: false, TimeSpan.FromSeconds(3)),
            "The browser window could not be restored after the extension reconnected.");
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.False(WindowCapture.IsIconic(hwnd), "The browser was minimized again after the extension reconnected.");
    }
}

/// <summary>
/// The restore path of #427: while the browser is paused, a student bringing its
/// window back (taskbar click) gets it minimized again, on every attempt, until
/// the extension reconnects. Relies on the real foreground hook's MINIMIZEEND
/// events, so it runs in the non-blocking Visual lane like
/// <see cref="ReMinimizeOnRestoreTests"/>.
///
/// Its control step, a restore after the reconnect that sticks, also catches a
/// minimize from the pause that lands after it lifted (#474): a hand-over the
/// overlay got to only after the reconnect, or a second minimize asked of the
/// window for one restore, which Notepad carried out after the spec's restore.
/// The failure messages list the browser's minimizes and restores to tell them
/// apart.
/// </summary>
[Trait("Category", "Visual")]
[Collection(AgentE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class ExtensionOffReMinimizeTests
{
    private const int RestoreCycles = 3;

    private readonly BackendFixture _backend;
    public ExtensionOffReMinimizeTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task RestoringThePausedBrowser_GetsItMinimizedAgain_UntilTheExtensionReconnects()
    {
        var api = new BackendClient(_backend.Url);
        await using var run = await BrowserPauseRun.StartAsync(_backend.Url, api);
        var hwnd = run.BrowserWindow;
        using var sampler = new ForegroundSampler(run.Agent.Pid);
        sampler.Name(hwnd, "browser");

        await run.Extension.DisconnectAsync();
        await run.WaitForGuardAsync(
            g => g.State == "Paused", BrowserPauseRun.Grace + TimeSpan.FromSeconds(5), "the browser to be paused");
        Assert.True(
            await BrowserPauseRun.WaitForIconicAsync(hwnd, expected: true, TimeSpan.FromSeconds(5)),
            "The browser was paused but its open window was not minimized.");

        for (var i = 0; i < RestoreCycles; i++)
        {
            WindowCapture.RestoreWindow(hwnd);
            Assert.True(
                await BrowserPauseRun.WaitForIconicAsync(hwnd, expected: true, TimeSpan.FromSeconds(5)),
                $"The paused browser stayed restored after restore #{i + 1} — the pause is not enforced on restore.");
        }

        // Control: once the extension is back, a restore sticks — so the
        // re-minimizes above were the pause, not a restore that never happened.
        sampler.Mark("reconnecting the extension");
        await run.Extension.ConnectAsync();
        await run.WaitForGuardAsync(g => g.State == "Present", TimeSpan.FromSeconds(5), "the pause to lift");
        sampler.Mark("restoring the browser after the reconnect");
        WindowCapture.RestoreWindow(hwnd);
        Assert.True(
            await BrowserPauseRun.WaitForIconicAsync(hwnd, expected: false, TimeSpan.FromSeconds(5)),
            $"After the extension reconnected, restoring the browser did not un-minimize it. Foreground: {sampler.Describe()}");
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(
            WindowCapture.IsIconic(hwnd),
            $"The browser was minimized again after the extension reconnected (#474). Foreground: {sampler.Describe()}");
    }
}

/// <summary>
/// #450: while the browser is paused, the paused-browser overlay must have the
/// keyboard, never the minimized browser. The agent may not take the foreground
/// itself, and the pause minimized the browser from the agent: a window minimized
/// that way either stays the foreground window and keeps what the student types
/// (what #441 measured for the block overlay: 6 of 6 letters reached it), or
/// hands the foreground to whatever window is below it. Notepad does the latter:
/// on the agent without the fix the window below it, an allowed app, came
/// forward, and the agent hid the overlay for it.
///
/// Keystrokes go to the foreground window, so the spec asserts that one: the
/// agent's full-screen overlay, after the pause and after the student activates
/// the minimized browser again (the first thing a click on its taskbar button
/// does). It never types: the stand-in browser is a real Notepad, which is no
/// window a spec may type into. Real foreground changes on a headless desktop are
/// the flaky part, so this runs in the non-blocking Visual lane, like
/// <see cref="ExtensionOffReMinimizeTests"/>. A person using the desktop while it
/// runs moves the foreground too; a failure message says when that happened.
///
/// #475: the agent takes a paused browser activated while minimized down by having
/// it restore and then minimize itself, and the browser carries the minimize out
/// whenever its app gets to it. The restore raises MINIMIZEEND, a second show of
/// the overlay while that minimize is still on its way. That show put the overlay
/// back in the topmost band until its own hand-over took it out again, and Windows
/// gives the foreground of a window that goes down to the first window from the
/// top that is not topmost: a browser that went down meanwhile handed the keyboard
/// past the overlay, to an allowed app of the developer's, and the agent hid the
/// overlay for it. That took the right moment at the speed of this box's Notepad
/// and agent, so
/// <see cref="WhileThePausedBrowserIsSlowToGoDown_TheOverlayStillGetsTheKeyboard"/>
/// holds both for that moment, as a busy browser and a busy agent would.
///
/// #477: once out of the topmost band, the overlay is put back at the top of the
/// others when another app's window came above it. From the agent, which does not
/// have the foreground, <c>HWND_TOP</c> leaves it below the windows of the app that
/// has it, so the agent took it through the band to get there, and a browser that
/// went down meanwhile handed the keyboard past it: on this box to an allowed File
/// Explorer window, and only when that one happened to be above the overlay.
/// <see cref="WithAnotherAppsWindowAboveTheOverlay_ThePausedBrowserGoingDownStillHandsItTheKeyboard"/>
/// puts such a window there every time: one of its own that shares the browser's
/// input, brought above the overlay while the agent is held right after it took the
/// overlay out of the band.
/// </summary>
[Trait("Category", "Visual")]
[Collection(AgentE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class ExtensionOffKeyboardTests
{
    private const string ActivatingThePausedBrowser = "activating the paused browser";

    // How long the spec holds the browser's app once the agent restored it: about
    // as long as the agent takes to show the overlay again for that restore, and
    // well within the moment the agent waits for the keyboard to come to it.
    private static readonly TimeSpan HoldTheBrowser = TimeSpan.FromMilliseconds(200);

    private readonly BackendFixture _backend;
    public ExtensionOffKeyboardTests(BackendFixture backend) => _backend = backend;

    // When the spec last pressed a key of its own (WindowCapture.LastInputTick).
    private uint _ownInput;

    [Fact]
    public async Task WhileTheBrowserIsPaused_TheOverlayHasTheKeyboard_NotTheMinimizedBrowser()
    {
        var api = new BackendClient(_backend.Url);
        await using var run = await BrowserPauseRun.StartAsync(_backend.Url, api);
        using var sampler = new ForegroundSampler(run.Agent.Pid);
        sampler.Name(run.BrowserWindow, "browser");

        await PauseWhileTheStudentIsInTheBrowserAsync(run, sampler);

        // Then they click the paused browser's taskbar button, which first
        // activates it while it is still minimized.
        await ActivateThePausedBrowserAsync(run, sampler);
        await AssertTheOverlayHasTheKeyboardAsync(run, sampler, ActivatingThePausedBrowser);
    }

    [Fact]
    public async Task WhileThePausedBrowserIsSlowToGoDown_TheOverlayStillGetsTheKeyboard()
    {
        var api = new BackendClient(_backend.Url);
        await using var run = await BrowserPauseRun.StartAsync(_backend.Url, api);
        var hwnd = run.BrowserWindow;
        using var sampler = new ForegroundSampler(run.Agent.Pid);
        sampler.Name(hwnd, "browser");
        await PauseWhileTheStudentIsInTheBrowserAsync(run, sampler);
        var overlay = WindowCapture.FindFullScreenWindow(run.Agent.Pid);

        // The agent restores the browser the student activated, takes the overlay
        // out of the topmost band and asks the browser to minimize itself. The
        // browser's app is busy and gets to that a moment later: the spec holds it
        // from the end of the restore, its MINIMIZEEND, which shows the overlay again.
        await ActivateThePausedBrowserAsync(run, sampler);
        Assert.True(
            await WaitUntilAsync(
                () => sampler.SawSince(ActivatingThePausedBrowser, hwnd, "restored") && !WindowCapture.IsTopmost(overlay),
                TimeSpan.FromSeconds(3)),
            "The agent did not restore the paused browser and take the overlay out of the topmost band to take the " +
            $"keyboard over from it. Foreground: {sampler.Describe()}");
        var backOnTop = false;
        SuspendedProcess? agentHeld = null;
        try
        {
            using (SuspendedProcess.Suspend(WindowCapture.ProcessIdOf(hwnd)))
            {
                var held = Stopwatch.StartNew();
                while (held.Elapsed < HoldTheBrowser && !backOnTop)
                {
                    // Back in the topmost band while the browser has yet to go down,
                    // the overlay is passed over when it does. The agent is held there
                    // too, as a busy UI thread would be, until the browser went down.
                    // Even for a moment: no hand-over takes the overlay back through
                    // the band on its way to the top of the others (#477).
                    backOnTop = BackOnTop(overlay, hwnd);
                    if (backOnTop)
                        agentHeld = SuspendedProcess.Suspend(run.Agent.Pid);
                }
            }
            Assert.True(
                await WaitUntilAsync(() => WindowCapture.IsIconic(hwnd), TimeSpan.FromSeconds(3)),
                $"The paused browser did not go down once its app got to it. Foreground: {sampler.Describe()}");
            await Task.Delay(100);
        }
        finally
        {
            agentHeld?.Dispose();
        }

        await AssertTheOverlayHasTheKeyboardAsync(
            run, sampler, "a paused browser slow to go down went down",
            backOnTop ? " The overlay was back in the topmost band before the browser went down (#475)." : "");
        Assert.False(
            backOnTop,
            "The overlay went back in the topmost band while the paused browser had yet to carry out the minimize " +
            "that hands it the keyboard: a browser that goes down then hands the keyboard past it (#475). " +
            $"Foreground: {sampler.Describe()}");
    }

    [Fact]
    public async Task WithAnotherAppsWindowAboveTheOverlay_ThePausedBrowserGoingDownStillHandsItTheKeyboard()
    {
        var api = new BackendClient(_backend.Url);
        await using var run = await BrowserPauseRun.StartAsync(_backend.Url, api);
        var browser = run.BrowserWindow;
        using var sampler = new ForegroundSampler(run.Agent.Pid);
        sampler.Name(browser, "browser");
        await PauseWhileTheStudentIsInTheBrowserAsync(run, sampler);
        var overlay = WindowCapture.FindFullScreenWindow(run.Agent.Pid);

        // Another app's window on screen, shown without the keyboard, which raises
        // nothing the agent judges. It shares the browser's input, so while the
        // browser has the foreground it counts as one of the browser's windows for
        // where Windows puts the windows of an app without the foreground, as the
        // agent is: HWND_TOP from the agent leaves the overlay below it (#477).
        using var other = await OffListWindow.OpenAsync(
            "Anchor e2e other app", TimeSpan.FromSeconds(10), activate: false);
        sampler.Name(other.Handle, "other app");
        Assert.True(other.ShareInputWith(browser), "The other app's window could not share the stand-in browser's input.");

        // The agent restores the browser the student activated and takes the overlay
        // out of the topmost band. It is held right then, as a busy agent would be.
        Assert.True(WindowCapture.IsTopmost(overlay), "The paused-browser overlay was not in the topmost band to start with.");
        var backInTheBand = false;
        var placedAgain = false;
        IDisposable? agentHeld = null;
        Task<bool>? activating = null;
        try
        {
            using (var outOfTheBand = new SuspendWhen(
                       () => !WindowCapture.IsTopmost(overlay), () => SuspendedProcess.Suspend(run.Agent.Pid)))
            {
                // On a thread of its own: activating the browser takes the keyboard from
                // the overlay, which waits on the agent's UI thread, and the agent is
                // held from the moment it takes the overlay out of the band.
                sampler.Mark(ActivatingThePausedBrowser);
                activating = Task.Factory.StartNew(
                    () => ActivateWithRetries(browser), CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Assert.True(
                    outOfTheBand.Wait(TimeSpan.FromSeconds(3)),
                    "The agent did not take the overlay out of the topmost band to take the keyboard over from the " +
                    $"paused browser. Foreground: {sampler.Describe()}");
                agentHeld = outOfTheBand.Stop();
            }

            // The browser's app finishes the restore the agent asked for, and the other
            // app's window comes above the overlay, as one of its windows does.
            Assert.True(
                await WaitUntilAsync(
                    () => sampler.SawSince(ActivatingThePausedBrowser, browser, "restored") && !WindowCapture.IsIconic(browser),
                    TimeSpan.FromSeconds(3)),
                $"The browser's app did not finish the restore the agent asked for. Foreground: {sampler.Describe()}");
            await Task.Delay(100);
            Assert.False(
                WindowCapture.IsIconic(browser) || sampler.SawSince(ActivatingThePausedBrowser, browser, "minimized"),
                "The paused browser went down before the other app's window came above the overlay: the spec did not " +
                $"hold the agent in time to set the scene. Foreground: {sampler.Describe()}");
            Assert.True(
                other.BringToTopWithoutActivating() && WindowCapture.IsAbove(other.Handle, overlay),
                $"The other app's window did not come above the overlay. Foreground: {sampler.Describe()}");

            // The browser's app is busy, and gets to the minimize the agent asks a
            // moment later. The agent goes on: it asks for that minimize, and puts the
            // overlay above every app window once more. Should it go through the topmost
            // band to get there, the overlay is held there, as a busy UI thread would
            // hold it, until the browser went down.
            using (SuspendedProcess.Suspend(WindowCapture.ProcessIdOf(browser)))
            {
                using var inTheBand = new SuspendWhen(
                    () => WindowCapture.IsTopmost(overlay) && !WindowCapture.IsIconic(browser),
                    () => SuspendedThread.Of(overlay));
                agentHeld?.Dispose();
                agentHeld = null;
                placedAgain = await WaitUntilAsync(
                    () => inTheBand.HasHeld || WindowCapture.IsAbove(overlay, other.Handle), TimeSpan.FromSeconds(1));
                agentHeld = inTheBand.Stop();
                backInTheBand = agentHeld is not null;
            }
            Assert.True(
                await WaitUntilAsync(() => WindowCapture.IsIconic(browser), TimeSpan.FromSeconds(3)),
                "The paused browser did not go down once its app got to it." +
                (backInTheBand
                    ? " The agent took the overlay through the topmost band to get it above the other app's window, " +
                      "and held there, it asked the browser to go down no sooner (#477)."
                    : "") +
                $" Foreground: {sampler.Describe()}");
            await Task.Delay(100);
        }
        finally
        {
            agentHeld?.Dispose();
        }
        Assert.True(
            activating is not null && await activating.WaitAsync(TimeSpan.FromSeconds(5)),
            "Windows refused to give the paused browser the foreground.");
        _ownInput = WindowCapture.LastInputTick();

        var note = backInTheBand
            ? " The agent took the overlay through the topmost band to get it above the other app's window (#477)."
            : placedAgain ? "" : " The agent did not put the overlay above the other app's window before the browser went down.";
        Assert.False(
            sampler.SawSince(ActivatingThePausedBrowser, other.Handle, "foreground"),
            "When the paused browser went down, the keyboard went to the other app's window, which was above the " +
            $"overlay: what the student types goes there (#477).{note}{Interference()} Foreground: {sampler.Describe()}");
        await AssertTheOverlayHasTheKeyboardAsync(
            run, sampler, "a paused browser went down with another app's window above the overlay", note);
        Assert.False(
            backInTheBand,
            "The agent took the overlay through the topmost band to get it above the other app's window while the " +
            "paused browser had yet to carry out the minimize that hands it the keyboard: a browser that goes down " +
            $"then hands the keyboard past it (#477). Foreground: {sampler.Describe()}");
    }

    /// <summary>
    /// The student is in the browser when they switch its extension off: the pause
    /// takes it down, and the overlay has the keyboard.
    /// </summary>
    private async Task PauseWhileTheStudentIsInTheBrowserAsync(BrowserPauseRun run, ForegroundSampler sampler)
    {
        sampler.Mark("switching the extension off");
        Assert.True(
            WindowCapture.BringToForeground(run.BrowserWindow),
            $"The stand-in browser could not be brought to the foreground (foreground: {DescribeForeground()}).");
        _ownInput = WindowCapture.LastInputTick();
        await run.Extension.DisconnectAsync();
        await run.WaitForGuardAsync(
            g => g.State == "Paused", BrowserPauseRun.Grace + TimeSpan.FromSeconds(5), "the browser to be paused");
        await AssertTheOverlayHasTheKeyboardAsync(run, sampler, "the pause");
    }

    /// <summary>
    /// Activates the paused browser while it is minimized, the first thing a click
    /// on its taskbar button does.
    /// </summary>
    private async Task ActivateThePausedBrowserAsync(BrowserPauseRun run, ForegroundSampler sampler)
    {
        sampler.Mark(ActivatingThePausedBrowser);
        var activated = false;
        for (var attempt = 0; attempt < 3 && !activated; attempt++)
        {
            activated = WindowCapture.ActivateOnce(run.BrowserWindow);
            if (!activated) await Task.Delay(200);
        }
        _ownInput = WindowCapture.LastInputTick();
        Assert.True(activated, "Windows refused to give the paused browser the foreground.");
    }

    /// <summary>
    /// <see cref="WindowCapture.ActivateOnce"/>, tried again a moment later should
    /// Windows refuse it, as <see cref="ActivateThePausedBrowserAsync"/> does.
    /// </summary>
    private static bool ActivateWithRetries(IntPtr hwnd)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (WindowCapture.ActivateOnce(hwnd)) return true;
            Thread.Sleep(200);
        }
        return false;
    }

    private static bool BackOnTop(IntPtr overlay, IntPtr browser) =>
        WindowCapture.IsTopmost(overlay) && !WindowCapture.IsIconic(browser);

    /// <summary>
    /// Holds the agent (<paramref name="hold"/>: its process, or its UI thread) the
    /// moment <paramref name="condition"/> holds, checked without pause on a thread of
    /// its own: a window can be somewhere for less than a millisecond. A spec holds
    /// the agent with it at a step of a hand-over, as a busy agent would be held
    /// (#477).
    /// </summary>
    private sealed class SuspendWhen : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _done = new();
        private volatile bool _stop;
        private IDisposable? _held;

        public SuspendWhen(Func<bool> condition, Func<IDisposable> hold)
        {
            _thread = new Thread(() =>
            {
                while (!_stop)
                {
                    if (!condition()) continue;
                    _held = hold();
                    break;
                }
                _done.Set();
            })
            { IsBackground = true, Priority = ThreadPriority.Highest, Name = "suspend-when" };
            _thread.Start();
        }

        /// <summary>Wait for the condition; whether the process is held.</summary>
        public bool Wait(TimeSpan timeout) => _done.Wait(timeout) && Volatile.Read(ref _held) is not null;

        /// <summary>Whether the process is held by now.</summary>
        public bool HasHeld => _done.IsSet && Volatile.Read(ref _held) is not null;

        /// <summary>Stop checking, and hand the hold over to the caller, if any.</summary>
        public IDisposable? Stop()
        {
            _stop = true;
            _thread.Join(TimeSpan.FromSeconds(5));
            return Interlocked.Exchange(ref _held, null);
        }

        public void Dispose()
        {
            Stop()?.Dispose();
            _done.Dispose();
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(5);
        }
        return condition();
    }

    /// <summary>
    /// After <paramref name="step"/>, the browser must be down and the overlay on
    /// screen with the foreground, and keep it: a window that only had it for a
    /// moment would not get what the student types next.
    /// </summary>
    private async Task AssertTheOverlayHasTheKeyboardAsync(
        BrowserPauseRun run, ForegroundSampler sampler, string step, string note = "")
    {
        var pid = run.Agent.Pid;
        var hwnd = run.BrowserWindow;
        var overlay = IntPtr.Zero;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            overlay = WindowCapture.FindFullScreenWindow(pid);
            if (WindowCapture.IsIconic(hwnd) && overlay != IntPtr.Zero && WindowCapture.ForegroundWindow() == overlay)
                break;
            await Task.Delay(100);
        }

        Assert.True(
            WindowCapture.IsIconic(hwnd),
            $"The browser was not minimized after {step}.{Interference()} Foreground: {sampler.Describe()}");
        Assert.True(
            overlay != IntPtr.Zero,
            $"The paused-browser overlay was not on screen after {step}.{note}{Interference()} The agent's windows:\n" +
            $"{WindowCapture.DescribeWindows(pid)}Foreground: {sampler.Describe()}");
        var hold = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        do
        {
            Assert.True(
                WindowCapture.ForegroundWindow() == overlay,
                $"After {step} the keyboard went to {DescribeForeground()} instead of the paused-browser overlay; " +
                $"the browser is {(WindowCapture.IsIconic(hwnd) ? "minimized" : "on screen")}. " +
                $"What the student types goes to that window (#450).{note}{Interference()} Foreground: {sampler.Describe()}");
            await Task.Delay(100);
        }
        while (DateTime.UtcNow < hold);
    }

    /// <summary>
    /// Says so in a failure message when someone used the desktop during the step:
    /// a click or key of theirs moves the foreground without the agent having a part
    /// in it, and an allowed app that comes forward hides the overlay, as it should.
    /// </summary>
    private string Interference() =>
        WindowCapture.LastInputTick() == _ownInput
            ? ""
            : " The desktop had input during this step that the spec did not send: someone using it moves the " +
              "foreground without the agent. Run the spec again without touching it.";

    private static string DescribeForeground()
    {
        var foreground = WindowCapture.ForegroundWindow();
        return $"{WindowCapture.GetProcessName(foreground)} [{WindowCapture.GetClassName(foreground)}]";
    }
}

/// <summary>
/// #452: while the browser is paused, blocking an off-list app must never bring
/// the paused browser back. The student was in the browser when its extension went
/// off, so the agent had its window down as the one to fall back to after a block.
/// Restored as that fallback, the paused browser came back on screen, and got the
/// foreground when the blocked app went down, until its own events paused it
/// again: the student saw it flash up and could type into it for a moment. The
/// browser must stay down, and the student stay on the paused-browser overlay,
/// with the keyboard: it is the one that says why the browser is gone and how to
/// get it back, and it does not offer the paused browser as a way back.
///
/// Watches the stand-in browser with the same restore events the agent listens to
/// (<see cref="ForegroundSampler.SawSince"/>), so a restore that lasts a moment
/// shows. Which overlay is up comes from <c>/status.overlay</c>. The off-list app
/// is the spec's own window (<see cref="OffListWindow"/>). Real foreground changes
/// on a headless desktop are the flaky part, so this runs in the non-blocking
/// Visual lane, like <see cref="ExtensionOffKeyboardTests"/>.
/// </summary>
[Trait("Category", "Visual")]
[Collection(AgentE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class ExtensionOffFallbackTests
{
    private const string OpeningTheOffListApp = "opening the off-list app";

    private readonly BackendFixture _backend;
    public ExtensionOffFallbackTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task WhileTheBrowserIsPaused_BlockingAnApp_NeverBringsThePausedBrowserBack()
    {
        var api = new BackendClient(_backend.Url);
        await using var run = await BrowserPauseRun.StartAsync(_backend.Url, api);
        var browser = run.BrowserWindow;
        // On the taskbar, not opened yet: it raises nothing the agent judges until it
        // is restored. Opened before the browser is set up as the fallback: a window
        // that comes up can move the foreground on, to an allowed app of the
        // developer's that would then be the fallback instead.
        using var offList = await OffListWindow.OpenAsync(
            "Anchor e2e off-list app", TimeSpan.FromSeconds(10), minimized: true);
        using var sampler = new ForegroundSampler(run.Agent.Pid);
        sampler.Name(browser, "browser");
        sampler.Name(offList.Handle, "off-list app");

        // The student works in the browser, so the agent has it down as the window to
        // fall back to after a block. Brought back from minimized without activating
        // it first: the agent judges it then even when it has the foreground already,
        // which raises no foreground change.
        // An allowed app of the developer's (the dev-only Claude, #372) that comes
        // forward right after would be the fallback instead: then try again.
        sampler.Mark("working in the browser");
        var working = false;
        for (var attempt = 0; attempt < 3 && !working; attempt++)
        {
            WindowCapture.MinimizeWithoutActivating(browser);
            Assert.True(await WaitUntilAsync(() => WindowCapture.IsIconic(browser), TimeSpan.FromSeconds(3)),
                "The stand-in browser could not be minimized.");
            WindowCapture.RestoreWithoutActivating(browser);
            Assert.True(await WaitUntilAsync(() => !WindowCapture.IsIconic(browser), TimeSpan.FromSeconds(3)),
                "The stand-in browser could not be restored.");
            Assert.True(
                WindowCapture.BringToForeground(browser),
                $"The stand-in browser could not be brought to the foreground. Foreground: {sampler.Describe()}");
            await Task.Delay(500);
            working = WindowCapture.ForegroundWindow() == browser &&
                      await FocusFallbackTests.LatestReportNamesAsync(
                          api, run.SessionId, TestConfig.AppProcessName, TimeSpan.FromSeconds(5));
        }
        Assert.True(
            working,
            "The agent did not judge the stand-in browser as the last app the student worked in, so it is not the " +
            $"window a block falls back to. Foreground: {sampler.Describe()}");

        // Its extension goes off. Once paused, the browser is down and the student is
        // on the paused-browser overlay, which took the keyboard over from it (#450).
        sampler.Mark("switching the extension off");
        await run.Extension.DisconnectAsync();
        await run.WaitForGuardAsync(
            g => g.State == "Paused", BrowserPauseRun.Grace + TimeSpan.FromSeconds(5), "the browser to be paused");
        await AssertOnThePausedBrowserOverlayAsync(run, sampler, "the pause");
        // No other allowed app came forward meanwhile, which would be the fallback
        // instead of the browser.
        await FocusFallbackTests.WaitForLatestReportAsync(
            api, run.SessionId, TestConfig.AppProcessName, "the browser, still the last app it judged");

        // Then an off-list app comes up, as it does from Alt+Tab, Start or by itself.
        sampler.Mark(OpeningTheOffListApp);
        offList.Restore();
        Assert.True(
            await WaitUntilAsync(() => offList.HasGoneDown, TimeSpan.FromSeconds(3)),
            $"The agent did not block the off-list app while the browser was paused. Foreground: {sampler.Describe()}");

        // The paused browser stays down the whole time, not even back for a moment.
        var watch = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < watch)
        {
            Assert.True(
                WindowCapture.IsIconic(browser),
                "Blocking the off-list app brought the paused browser back on screen: a paused browser is no " +
                $"window to fall back to (#452). Foreground: {sampler.Describe()}");
            await Task.Delay(50);
        }
        Assert.False(
            sampler.SawSince(OpeningTheOffListApp, browser, "restored"),
            "Blocking the off-list app restored the paused browser, if only for a moment: a paused browser is no " +
            $"window to fall back to (#452). Foreground: {sampler.Describe()}");

        // And the student is back on the paused-browser overlay, with the keyboard.
        await AssertOnThePausedBrowserOverlayAsync(run, sampler, "blocking the off-list app");
    }

    /// <summary>
    /// After <paramref name="step"/>, the browser must be down and the
    /// paused-browser overlay on screen with the foreground, and keep it.
    /// </summary>
    private static async Task AssertOnThePausedBrowserOverlayAsync(BrowserPauseRun run, ForegroundSampler sampler, string step)
    {
        var pid = run.Agent.Pid;
        var browser = run.BrowserWindow;
        var overlay = IntPtr.Zero;
        string? showing = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            overlay = WindowCapture.FindFullScreenWindow(pid);
            showing = (await run.Agent.TryGetStatusAsync())?.Overlay;
            if (WindowCapture.IsIconic(browser) && overlay != IntPtr.Zero &&
                WindowCapture.ForegroundWindow() == overlay && showing == "BrowserPaused")
                break;
            await Task.Delay(100);
        }

        Assert.True(WindowCapture.IsIconic(browser), $"The browser was not down after {step}. Foreground: {sampler.Describe()}");
        Assert.True(
            overlay != IntPtr.Zero,
            $"The overlay was not on screen after {step}. The agent's windows:\n" +
            $"{WindowCapture.DescribeWindows(pid)}Foreground: {sampler.Describe()}");
        Assert.True(
            showing == "BrowserPaused",
            $"After {step} the overlay shows '{showing ?? "<nothing>"}' instead of the paused browser: the student " +
            "does not learn why the browser is gone or how to get it back.");
        var hold = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        do
        {
            var foreground = WindowCapture.ForegroundWindow();
            Assert.True(
                foreground == overlay,
                $"After {step} the keyboard went to {WindowCapture.GetProcessName(foreground)} " +
                $"[{WindowCapture.GetClassName(foreground)}] instead of the paused-browser overlay. " +
                $"Foreground: {sampler.Describe()}");
            await Task.Delay(100);
        }
        while (DateTime.UtcNow < hold);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }
}

/// <summary>
/// One #427 scenario: a fresh Notepad standing in for the browser, the agent
/// launched with a private witness pipe and short pause timings, a fake extension
/// connected on that pipe, and a session joined with Notepad allowed.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class BrowserPauseRun : IAsyncDisposable
{
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan Repair = TimeSpan.FromSeconds(8);

    private readonly BackendClient _api;
    private Guid? _sessionId;
    private AgentProcess? _agent;

    private BrowserPauseRun(BackendClient api, FakeExtensionWitness extension, IntPtr window)
    {
        _api = api;
        Extension = extension;
        BrowserWindow = window;
    }

    public AgentProcess Agent => _agent ?? throw new InvalidOperationException("Agent not launched.");
    public FakeExtensionWitness Extension { get; }
    public IntPtr BrowserWindow { get; }
    public Guid SessionId => _sessionId ?? throw new InvalidOperationException("No session started.");

    public static async Task<BrowserPauseRun> StartAsync(string backendUrl, BackendClient api)
    {
        var pipeName = "anchor-witness-e2e-" + Guid.NewGuid().ToString("N");

        // The agent silently rejoins any running session the student is still in
        // as soon as it connects (#54), and that session's start-of-session sweep
        // would minimize the stand-in before ours begins. Every spec must end the
        // sessions it starts; say so here rather than fail later as a minimized
        // window.
        var stale = await api.GetRejoinableSessionIdsAsync();
        Assert.True(
            stale.Count == 0,
            $"The student is still in running session(s) {string.Join(", ", stale)} that an earlier spec " +
            "left behind; this agent would rejoin them on connect and sweep the stand-in browser. " +
            "End every session a spec starts.");

        // A fresh window must be up before the session starts (Win11 notepad is a
        // tabbed launcher, so kill leftovers first to guarantee a new window).
        KillNotepad();
        using (Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true })
               ?? throw new InvalidOperationException("Failed to launch notepad."))
        {
        }
        var hwnd = await WaitForNotepadWindowAsync(TimeSpan.FromSeconds(10));
        Assert.True(hwnd != IntPtr.Zero, "Notepad did not present a top-level window within 10s.");

        var run = new BrowserPauseRun(api, new FakeExtensionWitness(pipeName), hwnd);
        try
        {
            run._agent = AgentProcess.Launch(
                backendUrl,
                TestConfig.StudentOid,
                autoJoin: true,
                extraEnvironment: new Dictionary<string, string>
                {
                    ["Witness__PipeName"] = pipeName,
                    ["Witness__BrowserProcessName"] = TestConfig.AppProcessName,
                    ["Session__ExtensionGraceSeconds"] = ((int)Grace.TotalSeconds).ToString(),
                    ["Session__ExtensionRepairSeconds"] = ((int)Repair.TotalSeconds).ToString(),
                });
            await run.Agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));

            // The extension is installed and running before the lesson starts.
            await run.Extension.ConnectAsync();
            await run.WaitForGuardAsync(g => g.WitnessConnected, TimeSpan.FromSeconds(10), "the witness to connect");

            var classId = await api.FindClassIdAsync();
            var browserBundle = await api.FindBundleIdAsync(TestConfig.AppBundleName);
            var session = await api.StartSessionAsync(classId, new[] { browserBundle });
            run._sessionId = session.Id;

            var joined = await run.Agent.WaitForAsync(
                s => s.JoinedSessionId == session.Id
                     && (s.AllowedApps ?? Array.Empty<string>()).Contains(TestConfig.AppProcessName)
                     && s.ExtensionGuard?.State == "Present",
                TimeSpan.FromSeconds(10));
            Assert.True(
                joined?.JoinedSessionId == session.Id && joined.ExtensionGuard?.State == "Present",
                $"Agent did not join with the browser allowed and the extension present within 10s " +
                $"(joinedSessionId: {joined?.JoinedSessionId?.ToString() ?? "<none>"}, " +
                $"guard: {joined?.ExtensionGuard?.State ?? "<none>"}).");
            return run;
        }
        catch
        {
            await run.DisposeAsync();
            throw;
        }
    }

    /// <summary>Poll /status until the guard matches, or fail naming what was awaited.</summary>
    public async Task WaitForGuardAsync(Func<ExtensionGuardSnapshot, bool> predicate, TimeSpan timeout, string what)
    {
        var last = await Agent.WaitForAsync(s => s.ExtensionGuard is { } g && predicate(g), timeout);
        Assert.True(
            last?.ExtensionGuard is { } g && predicate(g),
            $"Timed out after {timeout.TotalSeconds:N0}s waiting for {what} " +
            $"(witnessConnected: {last?.ExtensionGuard?.WitnessConnected.ToString() ?? "<none>"}, " +
            $"state: {last?.ExtensionGuard?.State ?? "<none>"}).");
    }

    /// <summary>Poll until <paramref name="hwnd"/>'s iconic state matches, or time out.</summary>
    public static async Task<bool> WaitForIconicAsync(IntPtr hwnd, bool expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (WindowCapture.IsWindow(hwnd) && WindowCapture.IsIconic(hwnd) == expected)
                return true;
            await Task.Delay(100);
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_sessionId is { } id)
        {
            try { await _api.EndSessionAsync(id); } catch { /* best-effort */ }
        }
        await Extension.DisposeAsync();
        if (_agent is not null)
            await _agent.DisposeAsync();
        KillNotepad();
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

/// <summary>
/// The extension's end of the witness pipe, as the native host holds it: a
/// write-only client on the agent's pipe (<c>NamedPipeAgentLink</c>). Connected
/// means the extension is running; closing it is the extension switched off.
/// </summary>
internal sealed class FakeExtensionWitness : IAsyncDisposable
{
    private readonly string _pipeName;
    private NamedPipeClientStream? _pipe;

    public FakeExtensionWitness(string pipeName) => _pipeName = pipeName;

    public async Task ConnectAsync()
    {
        await DisconnectAsync();
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        try
        {
            // The agent re-arms its pipe server right after a drop; the connect
            // waits for it.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await pipe.ConnectAsync(cts.Token);
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
        _pipe = pipe;
    }

    public async Task DisconnectAsync()
    {
        var pipe = _pipe;
        _pipe = null;
        if (pipe is not null)
            await pipe.DisposeAsync();
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
