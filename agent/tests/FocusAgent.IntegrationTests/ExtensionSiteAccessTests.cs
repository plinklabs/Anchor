using System.Runtime.Versioning;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Asserting e2e for #524: an extension that is on, with its link up, but whose
/// "Site access" was turned down from "On all sites" on its page in Edge (before
/// the session, where nothing fires). The extension now says so itself
/// (<c>self_check</c>) on every session it is handed and on every change, and the
/// agent handles it like a switched-off extension: Edge is paused at once (the
/// overlay it shows is <see cref="ExtensionSiteAccessOverlayTests"/>'); the
/// overlay's action opens Edge for the repair window and has the extension open
/// its own page; a check that passes lifts the pause; and
/// the teacher is told (<c>TamperDetected{host_permission_revoked}</c>) only if the
/// student doesn't put it back in time.
///
/// Drives the real agent against the real backend over the real witness pipe, on
/// <see cref="BrowserPauseRun"/>'s stand-ins: a fake extension on a private pipe
/// that says what the real one says (extension/e2e/specs/self-check.spec.ts covers
/// the extension reading its own settings in Edge), and Notepad as the browser.
/// </summary>
[Collection(AgentE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class ExtensionSiteAccessTests
{
    private readonly BackendFixture _backend;
    public ExtensionSiteAccessTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task AnExtensionWithoutSiteAccess_PausesTheBrowser_UntilItIsPutBack_AndOnlyAnUnfixedOneReachesTheTeacher()
    {
        var api = new BackendClient(_backend.Url);
        await using var run = await BrowserPauseRun.StartAsync(_backend.Url, api);
        var hwnd = run.BrowserWindow;
        var agent = run.Agent;

        // 1. The extension checks its settings for the session the agent handed it:
        //    on all sites, and not allowed in InPrivate (Edge's default for every
        //    extension). Nothing is paused for that.
        await run.Extension.SendAsync(SelfCheck(run.SessionId, allSites: true));
        await run.WaitForGuardAsync(
            g => g.InPrivateAllowed == false, TimeSpan.FromSeconds(5), "the agent to hear the extension's check");
        await Task.Delay(TimeSpan.FromSeconds(1));
        var settled = await agent.TryGetStatusAsync();
        Assert.Equal("Present", settled?.ExtensionGuard?.State);
        Assert.False(WindowCapture.IsIconic(hwnd), "The browser was paused for an extension that may work on all sites.");

        // 2. Site access turned down: the browser is paused at once, well inside the
        //    grace period a dropped link gets, and its window comes down. (The
        //    overlay that says why is ExtensionSiteAccessOverlayTests': it depends
        //    on the real foreground.)
        await run.Extension.SendAsync(SelfCheck(run.SessionId, allSites: false));
        await run.WaitForGuardAsync(
            g => g.State == "Paused" && g.PauseReason == "NoSiteAccess" && g.SiteAccessLost,
            BrowserPauseRun.Grace - TimeSpan.FromSeconds(1),
            "the browser to be paused for the extension's site access");
        Assert.True(
            await BrowserPauseRun.WaitForIconicAsync(hwnd, expected: true, TimeSpan.FromSeconds(5)),
            "The browser was paused for its extension's site access but its open window was not minimized.");

        // 3. Put back in time: the pause lifts, the window can be brought back and
        //    stays up, and the teacher hears nothing.
        await run.Extension.SendAsync(SelfCheck(run.SessionId, allSites: true));
        await run.WaitForGuardAsync(
            g => g.State == "Present" && !g.SiteAccessLost, TimeSpan.FromSeconds(5), "the pause to lift");
        WindowCapture.RestoreWindow(hwnd);
        Assert.True(
            await BrowserPauseRun.WaitForIconicAsync(hwnd, expected: false, TimeSpan.FromSeconds(3)),
            "The browser window could not be restored after site access was put back.");
        await Task.Delay(BrowserPauseRun.SiteAccessReport + TimeSpan.FromSeconds(2));
        Assert.Equal(0, await SiteAccessReports.CountAsync(api, run.SessionId));
        Assert.False(WindowCapture.IsIconic(hwnd), "The browser was minimized again after site access was put back.");

        // 4. Taken away again, and the student presses the overlay's button: the
        //    browser opens for the repair window, and the extension is asked to open
        //    its own page, which the agent can't open from outside the browser.
        await run.Extension.SendAsync(SelfCheck(run.SessionId, allSites: false));
        await run.WaitForGuardAsync(
            g => g.State == "Paused" && g.PauseReason == "NoSiteAccess", TimeSpan.FromSeconds(5),
            "the browser to be paused again");
        Assert.True(await agent.TurnExtensionBackOnAsync(), "The agent refused to open the paused browser for repair.");
        await run.WaitForGuardAsync(
            g => g.State == "Repairing" && g.PauseReason == "NoSiteAccess", TimeSpan.FromSeconds(3),
            "the repair window to open");
        var asked = await run.Extension.WaitForAsync(
            m => m.GetProperty("type").GetString() == "open_extension_page"
                 && m.TryGetProperty("sessionId", out var s) && s.GetGuid() == run.SessionId,
            TimeSpan.FromSeconds(5));
        Assert.True(asked is not null, "The extension was never asked to open its own settings page.");

        // 5. Not put back in time: the teacher is told once, as site access, never
        //    as "switched off or removed".
        Assert.True(
            await SiteAccessReports.WaitForAsync(api, run.SessionId, 1, BrowserPauseRun.SiteAccessReport + TimeSpan.FromSeconds(10)),
            "Site access stayed taken away past the report time, but the teacher was not told (once).");
        Assert.Equal(0, await ExtensionOffReports.CountAsync(api, run.SessionId));

        // 6. Put back on the page the extension opened: the pause is over.
        await run.Extension.SendAsync(SelfCheck(run.SessionId, allSites: true));
        await run.WaitForGuardAsync(
            g => g.State == "Present", TimeSpan.FromSeconds(5), "the pause to lift after the repair");
        await Task.Delay(BrowserPauseRun.Repair);
        var after = await agent.TryGetStatusAsync();
        Assert.Equal("Present", after?.ExtensionGuard?.State);
        Assert.Equal(1, await SiteAccessReports.CountAsync(api, run.SessionId));
    }

    /// <summary>What the extension sends after reading its own settings (background.ts).</summary>
    internal static object SelfCheck(Guid sessionId, bool allSites) =>
        new { type = "self_check", sessionId, allSites, inPrivate = false };
}

/// <summary>
/// #524, what the student sees: a student in the browser when it is paused for
/// its extension's site access gets the site-access overlay (not the "switched
/// off" one), and it has the keyboard. Its copy in both languages is
/// <c>I18nTests</c>' and its rendering <c>OverlayVisualTests</c>'; this is the
/// live pause bringing it up. The overlay steps aside when an allowed app takes
/// the foreground, so this depends on the real foreground and runs in the
/// non-blocking Visual lane, like <see cref="ExtensionOffKeyboardTests"/>.
/// </summary>
[Trait("Category", "Visual")]
[Collection(AgentE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class ExtensionSiteAccessOverlayTests
{
    private readonly BackendFixture _backend;
    public ExtensionSiteAccessOverlayTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task ThePauseForSiteAccess_ShowsTheSiteAccessOverlay_WithTheKeyboard()
    {
        var api = new BackendClient(_backend.Url);
        await using var run = await BrowserPauseRun.StartAsync(_backend.Url, api);
        var hwnd = run.BrowserWindow;
        var pid = run.Agent.Pid;
        Assert.True(
            WindowCapture.BringToForeground(hwnd),
            "The stand-in browser could not be brought to the foreground.");

        await run.Extension.SendAsync(ExtensionSiteAccessTests.SelfCheck(run.SessionId, allSites: false));
        await run.WaitForGuardAsync(
            g => g.State == "Paused" && g.PauseReason == "NoSiteAccess", TimeSpan.FromSeconds(5),
            "the browser to be paused for the extension's site access");

        var overlay = IntPtr.Zero;
        StatusSnapshot? status = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            overlay = WindowCapture.FindFullScreenWindow(pid);
            status = await run.Agent.TryGetStatusAsync();
            if (WindowCapture.IsIconic(hwnd) && overlay != IntPtr.Zero
                && WindowCapture.ForegroundWindow() == overlay && status?.Overlay == "SiteAccessPaused")
                break;
            await Task.Delay(100);
        }

        Assert.True(WindowCapture.IsIconic(hwnd), "The paused browser was not taken down.");
        Assert.True(
            status?.Overlay == "SiteAccessPaused",
            $"The overlay did not show the site-access pause (showing: {status?.Overlay ?? "<nothing>"}). " +
            "An allowed app that takes the foreground hides it: run again without touching the desktop.");
        Assert.True(
            overlay != IntPtr.Zero && WindowCapture.ForegroundWindow() == overlay,
            $"The site-access overlay does not have the keyboard (foreground: " +
            $"{WindowCapture.GetProcessName(WindowCapture.ForegroundWindow())}).");
    }
}

/// <summary>
/// The "…limited the Anchor extension's access to websites" reports of a session,
/// as the teacher's dashboard gets them: <c>TamperDetected</c> events of kind
/// <c>host_permission_revoked</c> (#105, #524).
/// </summary>
internal static class SiteAccessReports
{
    public static async Task<int> CountAsync(BackendClient api, Guid sessionId) =>
        (await api.GetSessionEventsAsync(sessionId))
        .Count(e => e.Kind == "TamperDetected" && e.PayloadJson.Contains("host_permission_revoked"));

    /// <summary>Poll until the teacher has exactly <paramref name="expected"/> of them, or time out.</summary>
    public static async Task<bool> WaitForAsync(BackendClient api, Guid sessionId, int expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await CountAsync(api, sessionId) == expected) return true;
            await Task.Delay(300);
        }
        return await CountAsync(api, sessionId) == expected;
    }
}
