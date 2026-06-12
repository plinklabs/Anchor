using System.Diagnostics;
using System.Runtime.Versioning;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Asserting e2e for the session-start sweep (#104): an off-list app already
/// open when a session begins must be minimized at join, even though it never
/// fires a foreground <em>change</em> event. Drives the real agent against the
/// real backend and observes the sweep through <c>/status.startupSweep</c> — the
/// headless lever added alongside the feature, so this clears the "seen in a
/// running agent" bar rather than only a green unit test.
///
/// Note: exercising the real feature minimizes <em>every</em> off-list top-level
/// window on the desktop while it runs (that is the feature). Like the visual
/// specs, this drives the live desktop — expect other windows to be minimized
/// during the run.
/// </summary>
[Collection(AgentE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class SessionStartSweepTests
{
    private readonly BackendFixture _backend;
    public SessionStartSweepTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task OffListWindowOpenAtSessionStart_IsMinimizedBySweep()
    {
        var api = new BackendClient(_backend.Url);

        // A fresh off-list window must be up *before* the session starts — the
        // whole point of #104 is the app that predates enforcement. Win11 notepad
        // is a tabbed launcher, so kill leftovers first to guarantee a new window.
        KillNotepad();
        using var notepad = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true })
            ?? throw new InvalidOperationException("Failed to launch notepad.");
        try
        {
            Assert.True(
                await WaitForNotepadWindowAsync(TimeSpan.FromSeconds(10)),
                "Notepad did not present a top-level window within 10s.");

            await using var agent = AgentProcess.Launch(_backend.Url, TestConfig.StudentOid, autoJoin: true);
            await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));

            var classId = await api.FindClassIdAsync();
            // No bundles → notepad is off-list (only the baseline survives).
            var session = await api.StartSessionAsync(classId);
            try
            {
                var joined = await agent.WaitForAsync(
                    s => s.JoinedSessionId == session.Id, TimeSpan.FromSeconds(8));
                Assert.True(
                    joined?.JoinedSessionId == session.Id,
                    $"Agent did not auto-join within 8s (joinedSessionId: {joined?.JoinedSessionId?.ToString() ?? "<none>"}).");

                var swept = await agent.WaitForAsync(
                    s => s.StartupSweep is not null, TimeSpan.FromSeconds(5));
                var sweep = swept?.StartupSweep;
                Assert.True(sweep is not null, "Agent never reported a startup sweep on /status within 5s.");

                var minimized = sweep!.MinimizedProcesses ?? Array.Empty<string>();
                Assert.True(
                    minimized.Any(p => p.Equals("notepad", StringComparison.OrdinalIgnoreCase)),
                    $"Session-start sweep did not minimize notepad. examined={sweep.WindowsExamined}, " +
                    $"minimized=[{string.Join(", ", minimized)}].");
            }
            finally
            {
                await api.EndSessionAsync(session.Id);
            }
        }
        finally
        {
            KillNotepad();
        }
    }

    private static async Task<bool> WaitForNotepadWindowAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            // GetProcessesByName returns fresh handles each call, so MainWindowHandle
            // isn't stale-cached. Win11 may spawn the visible window under a pid that
            // differs from the launcher we started — match by name, not by pid.
            foreach (var p in Process.GetProcessesByName("notepad"))
            {
                using (p)
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                        return true;
                }
            }
            await Task.Delay(200);
        }
        return false;
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
