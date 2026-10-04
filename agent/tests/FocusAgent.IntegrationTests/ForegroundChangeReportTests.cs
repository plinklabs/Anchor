using System.Diagnostics;
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

        // Win11's notepad is a tabbed launcher, so kill leftovers first to
        // guarantee a fresh window.
        KillNotepad();
        using var notepad = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true })
            ?? throw new InvalidOperationException("Failed to launch notepad.");
        try
        {
            var hwnd = await WaitForNotepadWindowAsync(TimeSpan.FromSeconds(10));
            Assert.True(hwnd != IntPtr.Zero, "Notepad did not present a top-level window within 10s.");

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
                    "Agent never minimized the off-list Notepad after the session started (#104 sweep).");

                // Restoring it is a switch to Notepad: the agent re-minimizes it
                // and reports the blocked foreground change.
                WindowCapture.RestoreWindow(hwnd);
                var reported = await WaitForNotepadReportAsync(api, session.Id, TimeSpan.FromSeconds(10));
                Assert.True(
                    reported is not null,
                    "No ForegroundChange for Notepad reached the backend within 10s of restoring it. " +
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
            KillNotepad();
        }
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
