using System.Runtime.Versioning;
using System.Text.Json;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// What a foreground switch leaves behind for the teacher (#345): the
/// <c>ForegroundChange</c> events the real agent reports, as the backend stores
/// them and the past-session page shows them, name the app and say whether it
/// was blocked — and carry no window title and no executable path. Titles hold
/// document names, chat partners and search terms; paths hold the student's
/// Windows user name.
///
/// A real off-list Notepad drives the real foreground watcher, the same way as
/// <see cref="ReMinimizeOnRestoreTests"/>: the session-start sweep minimizes it,
/// and restoring it is a foreground switch the agent blocks and reports. Tagged
/// <c>Category=Visual</c> for the same reason as that spec — real foreground
/// events on a headless desktop are the flaky part — so it runs in the
/// non-blocking CI lane.
///
/// The session-start sweep moves the foreground too (#560): the window it hands
/// the keyboard to is the one the agent reports, and the one the student types in.
///
/// Note: like every spec that starts a session, this minimizes off-list windows
/// on the live desktop while it runs.
/// </summary>
[Trait("Category", "Visual")]
[Collection(AgentE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class ForegroundChangeReportTests
{
    private const string ForegroundChange = "ForegroundChange";

    private readonly BackendFixture _backend;
    public ForegroundChangeReportTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task ReportedForegroundChanges_NameTheApp_WithoutWindowTitleOrPath()
    {
        var api = new BackendClient(_backend.Url);

        var hwnd = await TestNotepad.OpenOnPrimaryMonitorAsync();
        try
        {
            await using var agent = AgentProcess.Launch(_backend.Url, TestConfig.StudentOid, autoJoin: true);
            await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));

            var classId = await api.FindClassIdAsync();
            // No bundles → notepad is off-list, so switching to it is blocked.
            var session = await api.StartSessionAsync(classId);
            try
            {
                var joined = await agent.WaitForAsync(
                    s => s.JoinedSessionId == session.Id, TimeSpan.FromSeconds(8));
                Assert.True(
                    joined?.JoinedSessionId == session.Id,
                    $"Agent did not auto-join within 8s (joinedSessionId: {joined?.JoinedSessionId?.ToString() ?? "<none>"}).");
                Assert.True(
                    await WaitForIconicAsync(hwnd, TimeSpan.FromSeconds(10)),
                    "Agent never minimized the off-list Notepad after the session started (#104 sweep); " +
                    $"Notepad is {WindowCapture.DescribePlacement(hwnd)}.");

                // Restoring it is a switch to Notepad: the agent re-minimizes it
                // and reports the blocked foreground change.
                WindowCapture.RestoreWindow(hwnd);
                var reported = await WaitForNotepadReportAsync(api, session.Id, TimeSpan.FromSeconds(10));
                Assert.True(
                    reported is not null,
                    "No ForegroundChange for Notepad reached the backend within 10s of restoring it " +
                    $"(Notepad: {WindowCapture.DescribePlacement(hwnd)}). " +
                    $"Events: {Describe(await api.GetSessionEventsAsync(session.Id))}.");

                using var payload = JsonDocument.Parse(reported!.PayloadJson);
                var fields = payload.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
                Assert.True(
                    payload.RootElement.GetProperty("blocked").GetBoolean(),
                    $"Notepad is off-list, so the switch should be reported as blocked: {reported.PayloadJson}");
                Assert.DoesNotContain("windowTitle", fields);
                Assert.DoesNotContain("exePath", fields);
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
    /// #560: the session-start sweep takes down the off-list app the student has in
    /// front, with their allowed Notepad right below it. Minimized from the agent,
    /// the off-list app stayed the foreground window: Windows raised the foreground
    /// event for Notepad, which the agent reported, while what the student typed
    /// went on reaching the minimized app. It minimizes itself now, so the letters
    /// the student types next reach Notepad, and the agent's report names the app
    /// that really has the foreground. Notepad is then the window the agent falls
    /// back to after a block (#557).
    ///
    /// The spec types only while its own Notepad or its own off-list window has the
    /// foreground, and puts Notepad's text back as it was: Windows 11 Notepad can
    /// open with a tab kept from an earlier session of whoever uses this desktop.
    /// </summary>
    [Fact]
    public async Task AfterTheSessionStartSweep_TheWindowBelowTheOffListApp_HasTheKeyboard_AndIsTheOneReported()
    {
        const int lettersTyped = 3;
        var api = new BackendClient(_backend.Url);

        var notepad = await TestNotepad.OpenOnPrimaryMonitorAsync();
        try
        {
            using var offList = await OffListWindow.OpenAsync("Anchor e2e off-list app", TimeSpan.FromSeconds(10));
            await using var agent = AgentProcess.Launch(_backend.Url, TestConfig.StudentOid, autoJoin: true);
            await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));
            using var sampler = new ForegroundSampler(agent.Pid);
            sampler.Name(notepad, "Notepad");
            sampler.Name(offList.Handle, "off-list app");

            // The student works in Notepad, then brings up the off-list app: it has
            // the foreground, with Notepad right below it.
            Assert.True(
                WindowCapture.BringToForeground(notepad),
                $"Notepad could not be brought to the foreground. Foreground: {sampler.Describe()}");
            Assert.True(
                WindowCapture.BringToForeground(offList.Handle),
                $"The off-list app could not be brought to the foreground. Foreground: {sampler.Describe()}");
            var ownInput = WindowCapture.LastInputTick();

            sampler.Mark("starting the session");
            var classId = await api.FindClassIdAsync();
            var appBundle = await api.FindBundleIdAsync(TestConfig.AppBundleName);
            var session = await api.StartSessionAsync(classId, new[] { appBundle });
            try
            {
                var joined = await agent.WaitForAsync(
                    s => s.JoinedSessionId == session.Id && s.StartupSweep is not null, TimeSpan.FromSeconds(10));
                Assert.True(
                    joined?.JoinedSessionId == session.Id && joined.StartupSweep is not null,
                    $"Agent did not join and sweep within 10s (joinedSessionId: {joined?.JoinedSessionId?.ToString() ?? "<none>"}).");
                Assert.True(
                    await WaitUntilAsync(() => offList.HasGoneDown, TimeSpan.FromSeconds(5)),
                    $"The session-start sweep did not minimize the off-list app (#104). Foreground: {sampler.Describe()}");
                // Windows hands the foreground on as the off-list app goes down. Where
                // it went, the letters below show.
                await WaitUntilAsync(() => WindowCapture.ForegroundWindow() != offList.Handle, TimeSpan.FromSeconds(2));
                var interference = Interference(ownInput);

                // The student goes on typing.
                sampler.Mark("typing");
                var textBox = IntPtr.Zero;
                await WaitUntilAsync(() => (textBox = TestNotepad.FindTextBox(notepad)) != IntPtr.Zero, TimeSpan.FromSeconds(5));
                Assert.True(textBox != IntPtr.Zero, "Notepad has no text box the spec can read.");
                var textBefore = TestNotepad.ReadText(textBox);
                Assert.True(textBefore is not null, "Notepad's text could not be read.");
                var keystrokesBefore = offList.KeystrokesReceived;
                var typed = 0;
                string? textAfter;
                try
                {
                    for (; typed < lettersTyped; typed++)
                    {
                        // Only ever into the spec's own windows: anything else is an app
                        // of the person running the suite.
                        var foreground = WindowCapture.ForegroundWindow();
                        if (foreground != notepad && foreground != offList.Handle)
                            break;
                        WindowCapture.PressLetter((char)('A' + typed));
                        await Task.Delay(100);
                    }
                    await Task.Delay(300);
                    textAfter = TestNotepad.ReadText(textBox);
                }
                finally
                {
                    var now = TestNotepad.ReadText(textBox);
                    if (now is not null && now != textBefore)
                        TestNotepad.SetText(textBox, textBefore!);
                }
                var reachedOffList = offList.KeystrokesReceived - keystrokesBefore;
                var reachedNotepad = (textAfter?.Length ?? 0) - textBefore!.Length;

                Assert.True(
                    typed == lettersTyped,
                    $"After the session-start sweep the keyboard went to {DescribeForeground()}, neither Notepad nor the " +
                    $"off-list app; the spec stopped typing after {typed} letters.{interference} Foreground: {sampler.Describe()}");
                Assert.True(
                    reachedOffList == 0,
                    $"{reachedOffList} of the {lettersTyped} letters typed after the session-start sweep reached the " +
                    $"minimized off-list app, which is still the foreground window ({WindowCapture.DescribePlacement(offList.Handle)}) " +
                    $"(#560).{interference} Foreground: {sampler.Describe()}");
                Assert.True(
                    reachedNotepad == lettersTyped,
                    $"{reachedNotepad} of the {lettersTyped} letters typed after the session-start sweep reached Notepad, " +
                    $"the window in front; the keyboard is with {DescribeForeground()}.{interference} Foreground: {sampler.Describe()}");

                // The teacher's feed says the same: of the spec's two apps, the one the
                // agent reported last is the one that has the foreground. An app of
                // whoever uses this desktop that comes forward meanwhile is judged and
                // reported too, so it is left out.
                var foregroundApp = WindowCapture.GetProcessName(WindowCapture.ForegroundWindow());
                Assert.True(
                    await LatestReportOfNamesAsync(
                        api, session.Id,
                        new[] { WindowCapture.GetProcessName(notepad), WindowCapture.GetProcessName(offList.Handle) },
                        foregroundApp, TimeSpan.FromSeconds(5)),
                    $"Of Notepad and the off-list app, the agent did not report {foregroundApp} last, the app that has the " +
                    $"foreground (#560). Events: {Describe(await api.GetSessionEventsAsync(session.Id))}. " +
                    $"Foreground: {sampler.Describe()}");

                // And Notepad is the window to fall back to: blocking the off-list app
                // now brings the student back to Notepad, not to the overlay (#557).
                //
                // The off-list app restores itself (OffListWindow.Restore): its own
                // thread restores the window in one go, so the minimize the agent's
                // block posts to it comes after. A restore from the spec's thread is
                // carried out in steps sent to the window's thread. The first step
                // activates the window, which the agent blocks, and on CI the block's
                // minimize landed between the steps and the rest undid it (#563).
                const string restoring = "restoring the off-list app";
                sampler.Mark(restoring);
                offList.Restore();
                // The restore is posted: the window is down and Notepad has the keyboard
                // until the off-list app gets to it. It may be back for only a moment.
                Assert.True(
                    await WaitUntilAsync(() => sampler.SawSince(restoring, offList.Handle, "restored"), TimeSpan.FromSeconds(5)),
                    $"The off-list app did not come back when the spec restored it ({WindowCapture.DescribePlacement(offList.Handle)}). " +
                    $"Foreground: {sampler.Describe()}");
                await WaitUntilAsync(
                    () => offList.HasGoneDown && WindowCapture.ForegroundWindow() == notepad, TimeSpan.FromSeconds(5));
                var overlay = (await agent.TryGetStatusAsync())?.Overlay;
                Assert.True(
                    offList.HasGoneDown && WindowCapture.ForegroundWindow() == notepad && overlay is null,
                    "After the session-start sweep gave Notepad the keyboard, blocking the off-list app did not bring the " +
                    $"student back to Notepad (off-list app: {WindowCapture.DescribePlacement(offList.Handle)}, " +
                    $"overlay: '{overlay ?? "<hidden>"}', Notepad: {WindowCapture.DescribePlacement(notepad)}). " +
                    $"Foreground: {sampler.Describe()}");
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
    /// Whether, of the reports that name one of <paramref name="apps"/>, the agent's
    /// most recent names <paramref name="processName"/> within <paramref name="timeout"/>.
    /// </summary>
    private static async Task<bool> LatestReportOfNamesAsync(
        BackendClient api, Guid sessionId, IReadOnlyCollection<string> apps, string processName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            // recentEvents is newest first.
            var latest = (await api.GetSessionEventsAsync(sessionId))
                .Where(e => e.Kind == ForegroundChange)
                .Select(e => ProcessNameOf(e.PayloadJson))
                .FirstOrDefault(name => apps.Contains(name, StringComparer.OrdinalIgnoreCase));
            if (string.Equals(latest, processName, StringComparison.OrdinalIgnoreCase))
                return true;
            await Task.Delay(250);
        }
        while (DateTime.UtcNow < deadline);
        return false;
    }

    private static string? ProcessNameOf(string payloadJson)
    {
        using var payload = JsonDocument.Parse(payloadJson);
        return payload.RootElement.TryGetProperty("processName", out var name) ? name.GetString() : null;
    }

    /// <summary>
    /// Says so in a failure message when someone used the desktop since the spec's
    /// last key (<paramref name="ownInput"/>): a click or key of theirs moves the
    /// foreground without the agent having a part in it.
    /// </summary>
    private static string Interference(uint ownInput) =>
        WindowCapture.LastInputTick() == ownInput
            ? ""
            : " The desktop had input that the spec did not send: someone using it moves the foreground without the " +
              "agent. Run the spec again without touching it.";

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

    private static async Task<BackendClient.SessionEvent?> WaitForNotepadReportAsync(
        BackendClient api, Guid sessionId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var events = await api.GetSessionEventsAsync(sessionId);
            var report = events.FirstOrDefault(e => e.Kind == ForegroundChange && IsNotepad(e.PayloadJson));
            if (report is not null) return report;
            await Task.Delay(250);
        }
        return null;
    }

    private static bool IsNotepad(string payloadJson)
    {
        using var payload = JsonDocument.Parse(payloadJson);
        return payload.RootElement.TryGetProperty("processName", out var name) &&
               string.Equals(name.GetString(), "notepad", StringComparison.OrdinalIgnoreCase);
    }

    private static string Describe(IEnumerable<BackendClient.SessionEvent> events)
    {
        var lines = events.Select(e => $"{e.Kind} {e.PayloadJson}").ToList();
        return lines.Count > 0 ? string.Join("; ", lines) : "<none>";
    }

    private static async Task<bool> WaitForIconicAsync(IntPtr hwnd, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (WindowCapture.IsWindow(hwnd) && WindowCapture.IsIconic(hwnd))
                return true;
            await Task.Delay(100);
        }
        return false;
    }
}
