using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

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

    /// <summary>
    /// The box Notepad's text is in: the classic Notepad's <c>Edit</c>, or Windows
    /// 11 Notepad's <c>RichEditD2DPT</c>; 0 when there is none.
    /// </summary>
    public static IntPtr FindTextBox(IntPtr notepad)
    {
        var found = IntPtr.Zero;
        EnumChildWindows(notepad, (child, _) =>
        {
            var className = WindowCapture.GetClassName(child);
            if (className is "Edit" or "RichEditD2DPT")
            {
                found = child;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// The text in Notepad's text box (<see cref="FindTextBox"/>), or null when it
    /// can't be read. Windows 11 Notepad may open with a tab it kept from an earlier
    /// session, so a spec compares the text before and after it types, never with
    /// what it expects to be there, and puts the text back
    /// (<see cref="SetText"/>). Never put it in a message: it can be the text of
    /// whoever uses this desktop.
    /// </summary>
    public static string? ReadText(IntPtr textBox)
    {
        if (SendMessageTimeoutW(textBox, WmGetTextLength, IntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung, 2000, out var length) == IntPtr.Zero)
            return null;
        var buffer = new StringBuilder((int)length + 1);
        if (SendMessageTimeoutW(textBox, WmGetText, buffer.Capacity, buffer, SmtoAbortIfHung, 2000, out var copied) == IntPtr.Zero)
            return null;
        return buffer.ToString(0, (int)copied);
    }

    /// <summary>Put <paramref name="text"/> in Notepad's text box (<see cref="FindTextBox"/>).</summary>
    public static void SetText(IntPtr textBox, string text) =>
        SendMessageTimeoutW(textBox, WmSetText, IntPtr.Zero, text, SmtoAbortIfHung, 2000, out _);

    private const uint WmSetText = 0x000C;
    private const uint WmGetText = 0x000D;
    private const uint WmGetTextLength = 0x000E;
    private const uint SmtoAbortIfHung = 0x0002;

    private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(
        IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(
        IntPtr hwnd, uint msg, int wParam, StringBuilder lParam, uint flags, uint timeoutMs, out IntPtr result);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(
        IntPtr hwnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeoutMs, out IntPtr result);

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
