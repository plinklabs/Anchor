using System.Diagnostics;
using System.Runtime.Versioning;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// The real Notepad the agent e2e specs open as an app of the student's: allowed
/// through the dev "Notepad (dev)" bundle, off-list without it, or the stand-in
/// browser (<see cref="BrowserPauseRun"/>). Every spec opens it the same way: a
/// fresh window, on screen on the primary monitor.
///
/// Notepad opens where it was last closed, which can be a second screen someone
/// works on while the suite runs. Their clicks there cover or minimize it, and a
/// spec then fails as though the agent had done it (#538, #542). Where a spec's
/// failure message would blame the agent for Notepad being down, it says where
/// Notepad is (<see cref="WindowCapture.DescribePlacement"/>) and adds
/// <see cref="TakenDownFromOutside"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class TestNotepad
{
    /// <summary>
    /// For a failure message that blames the agent for Notepad being down, after
    /// where it is: the agent is not the only one that can take it down (#542).
    /// </summary>
    public const string TakenDownFromOutside =
        "If the agent did not take it down, something outside the run did, such as someone using this desktop.";

    /// <summary>
    /// Launch a fresh Notepad and show its window at its normal size on the
    /// primary monitor, without activating it. Win11's Notepad is a tabbed
    /// launcher that adds a tab to a running one, so any Notepad is killed first;
    /// <see cref="Kill"/> again when the spec is done. Fails the spec when no
    /// window comes up within 10 s, or it can't be put on the primary monitor.
    /// </summary>
    public static async Task<IntPtr> OpenOnPrimaryMonitorAsync()
    {
        Kill();
        using (Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true })
               ?? throw new InvalidOperationException("Failed to launch notepad."))
        {
        }
        try
        {
            var hwnd = await WaitForWindowAsync(TimeSpan.FromSeconds(10));
            Assert.True(hwnd != IntPtr.Zero, "Notepad did not present a top-level window within 10s.");
            Assert.True(
                await WindowCapture.PutOnPrimaryMonitorAsync(hwnd, always: true),
                $"Notepad could not be put on screen on the primary monitor ({WindowCapture.DescribePlacement(hwnd)}).");
            return hwnd;
        }
        catch
        {
            Kill();
            throw;
        }
    }

    /// <summary>Kill every Notepad: the spec's, and any leftover.</summary>
    public static void Kill()
    {
        foreach (var p in Process.GetProcessesByName("notepad"))
        {
            using (p)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            }
        }
    }

    private static async Task<IntPtr> WaitForWindowAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            // Matched by name, not by the launcher's pid: Win11's Notepad shows its
            // window from a process of its own.
            var hwnd = WindowCapture.FindMainWindowByProcessName("notepad");
            if (hwnd != IntPtr.Zero) return hwnd;
            await Task.Delay(200);
        }
        return IntPtr.Zero;
    }
}
