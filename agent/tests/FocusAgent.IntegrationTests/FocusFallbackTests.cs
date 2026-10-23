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
/// Note: like every spec that starts a session, this minimizes off-list windows
/// on the live desktop while it runs.
/// </summary>
[Trait("Category", "Visual")]
[Collection(AgentE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class FocusFallbackTests
{
    private const string ForegroundChange = "ForegroundChange";

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
            WindowCapture.MinimizeWindow(notepad);
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
                WindowCapture.MinimizeWindow(notepad);
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
    /// <paramref name="processName"/>: proof it has judged that window.
    /// </summary>
    private static async Task WaitForLatestReportAsync(BackendClient api, Guid sessionId, string processName, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        List<BackendClient.SessionEvent> events;
        do
        {
            events = await api.GetSessionEventsAsync(sessionId);
            // recentEvents is newest first.
            var latest = events.FirstOrDefault(e => e.Kind == ForegroundChange);
            if (latest is not null && Names(latest.PayloadJson, processName))
                return;
            await Task.Delay(250);
        }
        while (DateTime.UtcNow < deadline);
        Assert.Fail(
            $"The agent did not report {what} ({processName}) as the latest foreground change within 10s. " +
            $"Events: {string.Join("; ", events.Select(e => $"{e.Kind} {e.PayloadJson}"))}.");
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
