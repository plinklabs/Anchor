using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Asserting e2e for #428: the Windows file picker an allowed app opens — the
/// one Edge opens for an upload button, shown by <c>PickerHost</c> — stays usable
/// during a session, and goes down with its app whenever that app isn't allowed:
/// off the list, or a browser paused because its extension is off (#427).
///
/// The picker is the real one (<see cref="FilePicker"/>): Windows shows it in
/// PickerHost as a window owned by the app's window, the shape of the upload
/// dialog the agent blocked in the pilot. Notepad stands in for the app, allowed
/// through the dev "Notepad (dev)" bundle, so the run never touches the
/// developer's own Edge. Observes through window state.
///
/// The agent judges the picker on the real foreground hook's events. When the
/// picker opens, Windows usually gives it the foreground; restoring it from
/// minimized raises MINIMIZEEND either way, which is the lever both specs pull.
/// Real foreground events on a headless desktop are the flaky part, so this runs
/// in the non-blocking Visual lane, like <see cref="ReMinimizeOnRestoreTests"/>.
///
/// Note: like every spec that starts a session, this minimizes off-list windows
/// on the live desktop while it runs.
/// </summary>
[Trait("Category", "Visual")]
[Collection(AgentE2ECollection.Name)]
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class FilePickerTests
{
    private const string ForegroundChange = "ForegroundChange";
    private static readonly TimeSpan PickerTimeout = TimeSpan.FromSeconds(10);

    private readonly BackendFixture _backend;
    public FilePickerTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task AnAllowedAppsFilePicker_StaysUsable_AndGoesDownWithTheApp_OnceItIsOffTheList()
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
            var app = await WaitForNotepadWindowAsync(TimeSpan.FromSeconds(10));
            Assert.True(app != IntPtr.Zero, "Notepad did not present a top-level window within 10s.");

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
                Assert.False(WindowCapture.IsIconic(app), "The allowed app was minimized at session start.");

                // 1. The student clicks an upload button: the app opens the file picker,
                //    and it stays on screen.
                using var picker = await FilePicker.OpenForAsync(app, PickerTimeout);
                await AssertStaysUsableAsync(
                    picker, TimeSpan.FromSeconds(2),
                    $"The file picker the allowed app opened ({picker.HostProcessName}) was taken down — " +
                    "a dialog an allowed app owns must be allowed with it (#428).");

                // 2. Every visit is judged again (MINIMIZEEND): still the allowed app.
                for (var i = 0; i < 2; i++)
                {
                    await BringBackFromMinimizedAsync(picker);
                    await AssertStaysUsableAsync(
                        picker, TimeSpan.FromSeconds(1.5),
                        $"The allowed app's file picker ({picker.HostProcessName}) was minimized after restore #{i + 1} (#428).");
                }

                // The teacher sees the app, not the Windows process that shows its picker.
                var events = await api.GetSessionEventsAsync(session.Id);
                Assert.DoesNotContain(events, e => e.Kind == ForegroundChange && Names(e.PayloadJson, picker.HostProcessName));

                // 3. The teacher takes the app off the list. Its picker is no way around
                //    that: when it comes back, the app goes down and the picker with it.
                await api.UpdateBundlesAsync(session.Id, Array.Empty<Guid>());
                var offList = await agent.WaitForAsync(s => !AllowsApp(s), TimeSpan.FromSeconds(5));
                Assert.True(offList is not null && !AllowsApp(offList), "The agent still allows the app after the bundle was removed.");

                await BringBackFromMinimizedAsync(picker, waitForRestore: false);
                Assert.True(
                    await WaitUntilAsync(() => WindowCapture.IsIconic(app) && !picker.IsUsable, TimeSpan.FromSeconds(5)),
                    "The off-list app's file picker came back without the app going down " +
                    $"(app iconic: {WindowCapture.IsIconic(app)}, picker usable: {picker.IsUsable}).");
                await Task.Delay(TimeSpan.FromSeconds(1));
                Assert.False(picker.IsUsable, "The off-list app's file picker came back on screen.");
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

    [Fact]
    public async Task APausedBrowsersFilePicker_IsNoWayAroundThePause()
    {
        var api = new BackendClient(_backend.Url);
        await using var run = await BrowserPauseRun.StartAsync(_backend.Url, api);
        var browser = run.BrowserWindow;

        // While the extension is on, the browser's picker is the browser's: usable.
        using var picker = await FilePicker.OpenForAsync(browser, PickerTimeout);
        await AssertStaysUsableAsync(
            picker, TimeSpan.FromSeconds(2),
            $"The allowed browser's file picker ({picker.HostProcessName}) was taken down (#428).");

        // The extension is switched off: the browser is paused, and its picker goes
        // down with it.
        await run.Extension.DisconnectAsync();
        await run.WaitForGuardAsync(
            g => g.State == "Paused", BrowserPauseRun.Grace + TimeSpan.FromSeconds(5), "the browser to be paused");
        Assert.True(
            await WaitUntilAsync(() => WindowCapture.IsIconic(browser) && !picker.IsUsable, TimeSpan.FromSeconds(5)),
            "The browser was paused but its file picker stayed on screen " +
            $"(browser iconic: {WindowCapture.IsIconic(browser)}, picker usable: {picker.IsUsable}).");

        // The picker coming back on its own is no way around the pause either.
        await BringBackFromMinimizedAsync(picker, waitForRestore: false);
        Assert.True(
            await WaitUntilAsync(() => !picker.IsUsable, TimeSpan.FromSeconds(5)),
            "The paused browser's file picker came back on screen (#427 + #428).");
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(picker.IsUsable, "The paused browser's file picker came back on screen (#427 + #428).");
        Assert.True(WindowCapture.IsIconic(browser), "The paused browser came back on screen with its picker.");

        // Control: once the extension is back, the same restore sticks, so the
        // picker going down above was the pause and not a restore that never happened.
        await run.Extension.ConnectAsync();
        await run.WaitForGuardAsync(g => g.State == "Present", TimeSpan.FromSeconds(5), "the pause to lift");
        WindowCapture.RestoreWindow(browser);
        Assert.True(
            await WaitUntilAsync(() => !WindowCapture.IsIconic(browser), TimeSpan.FromSeconds(5)),
            "The browser could not be restored after the extension reconnected.");
        await BringBackFromMinimizedAsync(picker);
        await AssertStaysUsableAsync(
            picker, TimeSpan.FromSeconds(1.5),
            "The browser's file picker was taken down after the extension reconnected.");
    }

    /// <summary>
    /// Minimize the picker and restore it — a visit the agent sees as
    /// MINIMIZEEND, whether or not Windows hands the picker the foreground.
    /// </summary>
    private static async Task BringBackFromMinimizedAsync(FilePicker picker, bool waitForRestore = true)
    {
        WindowCapture.MinimizeWindow(picker.Window);
        Assert.True(
            await WaitUntilAsync(() => WindowCapture.IsIconic(picker.Window), TimeSpan.FromSeconds(3)),
            "The file picker could not be minimized.");
        WindowCapture.RestoreWindow(picker.Window);
        if (waitForRestore)
            Assert.True(
                await WaitUntilAsync(() => picker.IsUsable, TimeSpan.FromSeconds(3)),
                "The file picker could not be restored from minimized.");
    }

    private static async Task AssertStaysUsableAsync(FilePicker picker, TimeSpan period, string failure)
    {
        var deadline = DateTime.UtcNow + period;
        while (DateTime.UtcNow < deadline)
        {
            Assert.True(picker.IsUsable, failure);
            await Task.Delay(100);
        }
        Assert.True(picker.IsUsable, failure);
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

    private static bool AllowsApp(StatusSnapshot status) =>
        (status.AllowedApps ?? Array.Empty<string>()).Contains(TestConfig.AppProcessName);

    private static bool Names(string payloadJson, string processName)
    {
        using var payload = JsonDocument.Parse(payloadJson);
        return payload.RootElement.TryGetProperty("processName", out var name) &&
               string.Equals(name.GetString(), processName, StringComparison.OrdinalIgnoreCase);
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
