using System.Diagnostics;
using System.Runtime.Versioning;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Real-window e2e for the #92 re-minimize-on-restore enforcement (the third
/// visual-enforcement surface deferred from #133). When the agent minimizes an
/// off-list window with <c>SW_MINIMIZE</c>, that window stays the *logical*
/// foreground window, so re-activating it (taskbar click) fires no
/// <c>EVENT_SYSTEM_FOREGROUND</c> — only <c>EVENT_SYSTEM_MINIMIZEEND</c>. The
/// fix added a second hook on MINIMIZEEND in <c>ForegroundWatcher</c>; this spec
/// proves the real watcher + real <c>ShowWindow(SW_MINIMIZE)</c> actually
/// re-minimize a restored window, which the unit-level
/// <c>FocusSessionControllerTests.Blocked_app_is_reminimized_on_every_reactivation_within_window</c>
/// (fake watcher) structurally can't reach.
///
/// Unlike the overlay/toast surfaces, this needs no self-test seam: the existing
/// real backend + <c>--auto-join</c> agent + a real off-list Notepad already
/// drive the real watcher end-to-end (same pattern as
/// <see cref="SessionStartSweepTests"/>). And it observes through window state
/// (<see cref="WindowCapture.IsIconic"/>) rather than a screenshot — more
/// reliable here than the foreground check the verify script flags as Win11-flaky.
///
/// Tagged <c>Category=Visual</c> so it runs in the same NON-BLOCKING CI lane as
/// the other deferred-visual specs until its flake rate is characterized
/// (real foreground events on a headless desktop are the flaky part). It still
/// needs the real backend, so it lives in <see cref="AgentE2ECollection"/>.
///
/// Note: exercising the real feature minimizes off-list windows on the live
/// desktop while it runs — expect other windows to be minimized during the run.
/// </summary>
[Trait("Category", "Visual")]
[Collection(AgentE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class ReMinimizeOnRestoreTests
{
    // How many restore→re-minimize cycles to drive. Design §5.2 says EVERY
    // reactivation must be re-enforced, so repeating it guards against a fix that
    // only handles the first restore (e.g. a one-shot dedupe by hwnd).
    private const int RestoreCycles = 3;

    private readonly BackendFixture _backend;
    public ReMinimizeOnRestoreTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task RestoringAMinimizedOffListWindow_GetsItReMinimized()
    {
        var api = new BackendClient(_backend.Url);

        // A fresh Notepad window must be up before the session starts. Win11's
        // notepad is a tabbed launcher, so kill leftovers first to guarantee a new
        // window (a merely-activated existing one fires no event to enforce on).
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
            // No bundles → notepad is off-list (only the baseline survives), so the
            // agent must minimize it and re-minimize every restore.
            var session = await api.StartSessionAsync(classId);
            var sessionEnded = false;
            try
            {
                var joined = await agent.WaitForAsync(
                    s => s.JoinedSessionId == session.Id, TimeSpan.FromSeconds(8));
                Assert.True(
                    joined?.JoinedSessionId == session.Id,
                    $"Agent did not auto-join within 8s (joinedSessionId: {joined?.JoinedSessionId?.ToString() ?? "<none>"}).");

                // The session-start sweep (#104) minimizes the already-open Notepad.
                // Wait for that baseline iconic state before driving restores.
                Assert.True(
                    await WaitForIconicAsync(hwnd, expected: true, TimeSpan.FromSeconds(10)),
                    "Agent never minimized the off-list Notepad after the session started (#104 sweep).");

                // #92: each restore-from-minimized must drive the window iconic
                // again. SW_RESTORE on another process's window is asynchronous, so
                // we can't reliably observe the brief non-iconic moment before the
                // agent re-minimizes (~20ms) — but on REGRESSED code (no MINIMIZEEND
                // hook) the window would simply stay restored and this wait would
                // time out. The post-session control below proves the restore isn't
                // a silent no-op, so "iconic again" here genuinely means re-enforced.
                for (var i = 0; i < RestoreCycles; i++)
                {
                    WindowCapture.RestoreWindow(hwnd);
                    Assert.True(
                        await WaitForIconicAsync(hwnd, expected: true, TimeSpan.FromSeconds(5)),
                        $"Notepad was not re-minimized within 5s after restore #{i + 1} — " +
                        "restore-from-minimized is not being re-enforced (#92).");
                }

                // Control: end the session (agent stops the watcher) and restore
                // once more. With no enforcement the window must un-minimize and
                // STAY restored — proving SW_RESTORE genuinely un-minimizes, so the
                // re-iconic results above were the agent re-enforcing, not a no-op.
                await api.EndSessionAsync(session.Id);
                sessionEnded = true;
                await agent.WaitForAsync(s => s.JoinedSessionId is null, TimeSpan.FromSeconds(8));

                WindowCapture.RestoreWindow(hwnd);
                Assert.True(
                    await WaitForIconicAsync(hwnd, expected: false, TimeSpan.FromSeconds(5)),
                    "After the session ended, restoring Notepad did not un-minimize it — " +
                    "SW_RESTORE is a no-op here, so the in-session re-minimize assertions are vacuous.");
                await Task.Delay(TimeSpan.FromSeconds(1));
                Assert.False(
                    WindowCapture.IsIconic(hwnd),
                    "Notepad was re-minimized after the session ended — enforcement should have stopped.");
            }
            finally
            {
                if (!sessionEnded)
                {
                    try { await api.EndSessionAsync(session.Id); } catch { /* best-effort */ }
                }
            }
        }
        finally
        {
            KillNotepad();
        }
    }

    /// <summary>Poll until <paramref name="hwnd"/>'s iconic state matches, or time out.</summary>
    private static async Task<bool> WaitForIconicAsync(IntPtr hwnd, bool expected, TimeSpan timeout)
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
