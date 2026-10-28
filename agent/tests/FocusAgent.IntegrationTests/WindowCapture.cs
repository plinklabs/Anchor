using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// Screenshot + window-probe helper for the visual-enforcement e2e (#133). The
/// focus overlay and the join-confirmation toast are pure WinUI 3 surfaces with
/// no <c>/status</c> field to poll, so the only way to assert they actually
/// render is to find their HWND and capture the pixels. This is the integration
/// suite's port of the capture logic in <c>scripts/dev/verify-overlay.ps1</c> /
/// <c>verify-toast.ps1</c>, reused across both visual specs.
///
/// Capture is <see cref="BitBlt"/> from the screen DC with the
/// <see cref="CAPTUREBLT"/> flag — deliberately not <c>PrintWindow</c>: WinUI 3
/// paints text + controls through DirectComposition surfaces that PrintWindow's
/// GDI back-buffer misses (they come out blank), whereas CAPTUREBLT asks the
/// screen DC to fold in layered/composed surfaces so the real content shows up.
/// The test process is made per-monitor DPI-aware first (static ctor) so
/// <see cref="GetWindowRect"/> and the screen BitBlt agree on physical pixels on
/// a scaled display — otherwise a window at virtual (1036,24) really lives at
/// (2072,48) on a 200% monitor and the capture grabs whatever is behind it.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowCapture
{
    static WindowCapture()
    {
        // Must run before any GetWindowRect / screen-DC call in this process so
        // the awareness sticks. The test host creates no UI before the first
        // capture, so touching this type (its static ctor) is early enough.
        try { SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2); }
        catch { /* already pinned by a manifest, or unsupported OS — best effort */ }
    }

    /// <summary>
    /// Find the agent's visible WinUI surface for <paramref name="pid"/>: the
    /// largest top-level window whose class is the WinUI 3 desktop class. In
    /// <c>--show-test-overlay</c> / <c>--show-test-toast</c> mode that single
    /// sizable window IS the overlay/toast (same heuristic as the verify
    /// scripts). Returns <see cref="IntPtr.Zero"/> if none has appeared yet.
    /// Matching on PID means a developer's own running agent never collides
    /// with the throwaway self-test process.
    /// </summary>
    public static IntPtr FindAgentWindow(int pid)
    {
        IntPtr found = IntPtr.Zero;
        var largestArea = 0;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var windowPid);
            if (windowPid != (uint)pid) return true;

            var className = new StringBuilder(256);
            GetClassNameW(hWnd, className, className.Capacity);
            if (!className.ToString().StartsWith("WinUIDesktop", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!GetWindowRect(hWnd, out var r)) return true;
            var area = (r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > largestArea)
            {
                largestArea = area;
                found = hWnd;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// The first <em>visible</em> top-level window of <paramref name="pid"/> whose
    /// class starts with <paramref name="classPrefix"/> and whose title is exactly
    /// <paramref name="title"/>, or <see cref="IntPtr.Zero"/>. The startup-crash spec
    /// (#407) uses it to find the crash surface, the WinUI crash dialog
    /// (<c>WinUIDesktop…</c>) or the Win32 fallback message box (<c>#32770</c>), both
    /// titled "Anchor", and not another window the crashed startup also opened
    /// (e.g. the guided extension install on a policy-locked box).
    /// </summary>
    public static IntPtr FindVisibleWindow(int pid, string classPrefix, string title)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var windowPid);
            if (windowPid != (uint)pid || !IsWindowVisible(hWnd)) return true;

            var className = new StringBuilder(256);
            GetClassNameW(hWnd, className, className.Capacity);
            if (!className.ToString().StartsWith(classPrefix, StringComparison.OrdinalIgnoreCase))
                return true;

            var text = new StringBuilder(256);
            GetWindowTextW(hWnd, text, text.Capacity);
            if (!string.Equals(text.ToString(), title, StringComparison.Ordinal))
                return true;

            found = hWnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Ask <paramref name="hwnd"/> to close (posts <c>WM_CLOSE</c>), the headless
    /// stand-in for a user clicking the window's close button. Works for a WinUI
    /// window and for an OK-only Win32 message box alike.
    /// </summary>
    public static void PostClose(IntPtr hwnd) => PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    /// <summary>
    /// Windows' resume notice (#456): <c>WM_POWERBROADCAST</c> with
    /// <c>PBT_APMRESUMESUSPEND</c>, sent after a resume the user caused (opening the
    /// lid). .NET's SystemEvents turns it into <c>PowerModeChanged(Resume)</c>.
    /// </summary>
    public const uint WmPowerBroadcast = 0x0218;
    public const int PbtApmResumeSuspend = 0x0007;

    /// <summary>
    /// Windows' unlock notice (#456): <c>WM_WTSSESSION_CHANGE</c> with
    /// <c>WTS_SESSION_UNLOCK</c>. SystemEvents turns it into
    /// <c>SessionSwitch(SessionUnlock)</c>.
    /// </summary>
    public const uint WmWtsSessionChange = 0x02B1;
    public const int WtsSessionUnlock = 0x0008;

    /// <summary>
    /// Post <paramref name="message"/> to <paramref name="pid"/>'s SystemEvents window,
    /// the hidden top-level window (class <c>.NET-BroadcastEventWindow.*</c>) that
    /// .NET's SystemEvents creates to receive Windows' power and session broadcasts, so
    /// the process handles it exactly as it handles the real one. Waits for the window
    /// to exist (SystemEvents creates it on the first subscription) and returns false
    /// if it never does.
    /// </summary>
    public static async Task<bool> PostToSystemEventsWindowAsync(int pid, uint message, int wParam, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var windows = new List<IntPtr>();
            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out var windowPid);
                if (windowPid == (uint)pid &&
                    GetClassName(hWnd).StartsWith(".NET-BroadcastEventWindow", StringComparison.Ordinal))
                {
                    windows.Add(hWnd);
                }
                return true;
            }, IntPtr.Zero);

            if (windows.Count > 0)
            {
                foreach (var hwnd in windows)
                    PostMessageW(hwnd, message, new IntPtr(wParam), IntPtr.Zero);
                return true;
            }
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(100);
        }
    }

    /// <summary>True while <paramref name="hwnd"/> is a valid window that is shown.</summary>
    public static bool IsVisible(IntPtr hwnd) => IsWindowNative(hwnd) && IsWindowVisible(hwnd);

    /// <summary>True while <paramref name="hwnd"/> is still a valid window handle.</summary>
    public static bool IsWindow(IntPtr hwnd) => IsWindowNative(hwnd);

    /// <summary>
    /// True while <paramref name="hwnd"/> is minimized. The window-state lever the
    /// #92 re-minimize spec asserts on instead of a screenshot: an off-list window
    /// the agent has minimized reads iconic, restoring it clears that, and the
    /// re-enforcement (MINIMIZEEND hook) must drive it iconic again.
    /// </summary>
    public static bool IsIconic(IntPtr hwnd) => IsIconicNative(hwnd);

    /// <summary>
    /// True while <paramref name="hwnd"/> is in the topmost band of the Z-order:
    /// the agent's overlay is, except while it takes the keyboard over from a
    /// window that goes down (#475).
    /// </summary>
    public static bool IsTopmost(IntPtr hwnd) => (GetWindowLongW(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;

    /// <summary>
    /// Restore <paramref name="hwnd"/> from minimized — the headless stand-in for a
    /// student clicking the taskbar entry of a just-minimized off-list window. This
    /// is the exact transition #92 fixed: it raises EVENT_SYSTEM_MINIMIZEEND (not
    /// FOREGROUND), which is the only event the agent gets to re-minimize on.
    /// </summary>
    public static void RestoreWindow(IntPtr hwnd) => ShowWindow(hwnd, SW_RESTORE);

    /// <summary>
    /// Minimize <paramref name="hwnd"/>, so a following <see cref="RestoreWindow"/>
    /// raises EVENT_SYSTEM_MINIMIZEEND for it: the #428 spec's lever for making the
    /// agent judge a file picker without waiting on Windows to grant it the
    /// foreground.
    /// </summary>
    public static void MinimizeWindow(IntPtr hwnd) => ShowWindow(hwnd, SW_MINIMIZE);

    /// <summary>
    /// Minimize <paramref name="hwnd"/> without activating another window. Once the
    /// test process has sent input (a spec that types, #441), Windows lets it set
    /// the foreground, and <see cref="MinimizeWindow"/> then hands the foreground to
    /// whatever window is next in the Z-order: on a developer's desktop an allowed
    /// app of their own, which the agent takes as the student's app.
    /// </summary>
    public static void MinimizeWithoutActivating(IntPtr hwnd) => ShowWindow(hwnd, SW_SHOWMINNOACTIVE);

    /// <summary>
    /// Restore <paramref name="hwnd"/> from minimized without activating it. Like
    /// <see cref="RestoreWindow"/> it raises EVENT_SYSTEM_MINIMIZEEND, so the agent
    /// judges the window, but the foreground stays where it was (#437).
    /// </summary>
    public static void RestoreWithoutActivating(IntPtr hwnd) => ShowWindow(hwnd, SW_SHOWNOACTIVATE);

    /// <summary>
    /// Hide <paramref name="hwnd"/>: the stand-in for a window that went away after
    /// it had the focus, like the taskbar's thumbnail flyout closing (#437).
    /// </summary>
    public static void HideWindow(IntPtr hwnd) => ShowWindow(hwnd, SW_HIDE);

    /// <summary>The primary taskbar's window (<c>Shell_TrayWnd</c>), or <see cref="IntPtr.Zero"/>.</summary>
    public static IntPtr FindTaskbar() => FindWindowW("Shell_TrayWnd", null);

    /// <summary>The window that has the foreground right now.</summary>
    public static IntPtr ForegroundWindow() => GetForegroundWindow();

    /// <summary>The window class of <paramref name="hwnd"/>, for failure messages.</summary>
    public static string GetClassName(IntPtr hwnd)
    {
        var className = new StringBuilder(256);
        GetClassNameW(hwnd, className, className.Capacity);
        return className.ToString();
    }

    /// <summary>
    /// Give <paramref name="hwnd"/> the foreground, the way a click on it would.
    /// The test process is a background process that Windows won't let take the
    /// foreground, so this sends a key with no meaning (<c>VK_NONAME</c>) first,
    /// which makes it the process that had the last input, and attaches to the
    /// foreground window's input queue (the same dance the agent's enforcer uses).
    /// Returns whether <paramref name="hwnd"/> ended up in the foreground.
    /// </summary>
    public static bool BringToForeground(IntPtr hwnd)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            ActivateOnce(hwnd);

            var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(500);
            while (DateTime.UtcNow < deadline)
            {
                if (GetForegroundWindow() == hwnd) return true;
                Thread.Sleep(50);
            }
        }
        return GetForegroundWindow() == hwnd;
    }

    /// <summary>
    /// One attempt of <see cref="BringToForeground"/>, without waiting for the
    /// result or trying again: for a window the agent takes down the moment it
    /// gets the foreground, where a second attempt would activate it again while
    /// it is minimized (#441). Returns whether Windows accepted the request.
    /// </summary>
    public static bool ActivateOnce(IntPtr hwnd)
    {
        // AttachThreadInput needs a message queue on the calling thread, and a
        // test's thread-pool thread may not have one yet: PeekMessage creates it.
        PeekMessageW(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);
        keybd_event(VK_NONAME, 0, 0, UIntPtr.Zero);
        keybd_event(VK_NONAME, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        var current = GetCurrentThreadId();
        var foreground = GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero ? 0u : GetWindowThreadProcessId(foreground, out _);
        var attached = false;
        try
        {
            if (foregroundThread != 0 && foregroundThread != current)
                attached = AttachThreadInput(current, foregroundThread, true);
            return SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
                AttachThreadInput(current, foregroundThread, false);
        }
    }

    /// <summary>
    /// Press and release the letter key <paramref name="letter"/> (A–Z), as the
    /// student typing would: it goes to whichever window has the keyboard. A spec
    /// checks that window first (<see cref="ProcessIdOf"/>) and only types into
    /// its own windows or the agent's, never into an app of the person running it.
    /// </summary>
    public static void PressLetter(char letter)
    {
        if (letter is < 'A' or > 'Z')
            throw new ArgumentOutOfRangeException(nameof(letter), letter, "Only the letter keys A-Z.");
        keybd_event((byte)letter, 0, 0, UIntPtr.Zero);
        keybd_event((byte)letter, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    /// <summary>
    /// Listen for foreground changes and minimize/restore transitions on the
    /// calling thread, which must pump messages (<see cref="PumpMessagesUntilQuit"/>)
    /// for the callbacks to arrive. Dispose to stop.
    /// </summary>
    public static IDisposable HookForeground(Action<IntPtr, string> onEvent)
    {
        WinEventProc callback = (_, eventType, hwnd, idObject, idChild, _, _) =>
        {
            if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return;
            onEvent(hwnd, eventType switch
            {
                EVENT_SYSTEM_FOREGROUND => "foreground",
                EVENT_SYSTEM_MINIMIZESTART => "minimized",
                EVENT_SYSTEM_MINIMIZEEND => "restored",
                _ => $"event 0x{eventType:X}",
            });
        };
        var foreground = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, callback, 0, 0, 0);
        var minimize = SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, callback, 0, 0, 0);
        return new WinEventHooks(callback, foreground, minimize);
    }

    private sealed class WinEventHooks(WinEventProc callback, params IntPtr[] hooks) : IDisposable
    {
        // Held so the delegate outlives the native hooks that call it.
        private readonly WinEventProc _callback = callback;

        public void Dispose()
        {
            foreach (var hook in hooks)
                if (hook != IntPtr.Zero) UnhookWinEvent(hook);
            GC.KeepAlive(_callback);
        }
    }

    /// <summary>The calling thread's native id, for <see cref="PostQuit"/>.</summary>
    public static uint CurrentThreadId() => GetCurrentThreadId();

    /// <summary>Pump the calling thread's messages until <see cref="PostQuit"/>.</summary>
    public static void PumpMessagesUntilQuit()
    {
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            DispatchMessageW(ref msg);
    }

    /// <summary>End <see cref="PumpMessagesUntilQuit"/> on the thread <paramref name="threadId"/>.</summary>
    public static void PostQuit(uint threadId) => PostThreadMessageW(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);

    /// <summary>The id of the process that shows <paramref name="hwnd"/>, or 0.</summary>
    public static int ProcessIdOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }

    /// <summary>
    /// The visible WinUI window of <paramref name="pid"/> that covers its whole
    /// monitor, or <see cref="IntPtr.Zero"/>: the agent's focus overlay, its only
    /// full-screen surface (#103). Allows the couple of pixels of rounding the
    /// overlay spec allows.
    /// </summary>
    public static IntPtr FindFullScreenWindow(int pid)
    {
        const int tolerancePx = 2;
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var windowPid);
            if (windowPid != (uint)pid || !IsWindowVisible(hWnd)) return true;
            if (!GetClassName(hWnd).StartsWith("WinUIDesktop", StringComparison.OrdinalIgnoreCase)) return true;
            if (!GetWindowRect(hWnd, out var r)) return true;
            var monitor = GetMonitorRectForWindow(hWnd);
            if (Math.Abs(r.Left - monitor.Left) > tolerancePx || Math.Abs(r.Top - monitor.Top) > tolerancePx ||
                Math.Abs(r.Right - r.Left - monitor.Width) > tolerancePx ||
                Math.Abs(r.Bottom - r.Top - monitor.Height) > tolerancePx)
                return true;
            found = hWnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// The first visible top-level window that <paramref name="owner"/> owns and
    /// that another process shows, or <see cref="IntPtr.Zero"/>: a dialog shown
    /// for the owner's app from outside it, like the file picker PickerHost shows
    /// for an Edge upload (#428).
    /// </summary>
    public static IntPtr FindWindowOwnedFromAnotherProcess(IntPtr owner)
    {
        GetWindowThreadProcessId(owner, out var ownerPid);
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            if (GetWindow(hWnd, GW_OWNER) != owner || !IsWindowVisible(hWnd)) return true;
            GetWindowThreadProcessId(hWnd, out var windowPid);
            if (windowPid == ownerPid) return true;
            found = hWnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>The name of the process that shows <paramref name="hwnd"/> (no extension).</summary>
    public static string GetProcessName(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return $"<exited pid {pid}>";
        }
    }

    /// <summary>
    /// The current top-level main-window handle of a running process named
    /// <paramref name="processName"/> (no extension), or <see cref="IntPtr.Zero"/>
    /// if none is up yet. Resolves a fresh <see cref="Process"/> each call so the
    /// handle isn't stale-cached, and matches by name not pid because Win11's
    /// notepad launcher spawns the visible window under a different pid.
    /// </summary>
    public static IntPtr FindMainWindowByProcessName(string processName)
    {
        foreach (var p in Process.GetProcessesByName(processName))
        {
            using (p)
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                    return p.MainWindowHandle;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>A window's on-screen rect, in physical pixels.</summary>
    public readonly record struct ScreenRect(int Left, int Top, int Width, int Height);

    /// <summary>Current on-screen rect of <paramref name="hwnd"/> (physical pixels).</summary>
    public static ScreenRect GetWindowScreenRect(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r))
            throw new InvalidOperationException("GetWindowRect failed for the agent window.");
        var w = r.Right - r.Left;
        var h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0)
            throw new InvalidOperationException($"Agent window has a non-positive rect ({w}x{h}).");
        return new ScreenRect(r.Left, r.Top, w, h);
    }

    /// <summary>
    /// Full bounds (incl. taskbar) of the monitor that currently contains
    /// <paramref name="hwnd"/>, in physical pixels. The lever the overlay spec
    /// uses to assert the surface covers its <em>entire</em> monitor (#103):
    /// compare the window rect against this. <c>rcMonitor</c> is the whole
    /// display rect — the work area minus the taskbar would be <c>rcWork</c>.
    /// </summary>
    public static ScreenRect GetMonitorRectForWindow(IntPtr hwnd)
    {
        var hMonitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(hMonitor, ref info))
            throw new InvalidOperationException("GetMonitorInfo failed for the agent window's monitor.");
        var r = info.rcMonitor;
        return new ScreenRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>
    /// Display scale <paramref name="hwnd"/> renders at (1.0 = 96 DPI = 100%,
    /// 1.25 = 120 DPI, 2.0 = 192 DPI). The agent's WinUI windows are per-monitor
    /// aware, so this is the scale of the monitor they sit on — the factor that
    /// turns a DIP size in the app into physical pixels in a capture. Lets a spec
    /// size a pixel-count threshold to the mark it is looking for instead of to
    /// one machine's scaling (#415).
    /// </summary>
    public static double GetWindowScale(IntPtr hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0)
            throw new InvalidOperationException("GetDpiForWindow failed for the agent window.");
        return dpi / 96.0;
    }

    /// <summary>Capture the window's current on-screen rect.</summary>
    public static Bitmap Capture(IntPtr hwnd) => CaptureRect(GetWindowScreenRect(hwnd));

    /// <summary>
    /// Park the mouse cursor in the top-left corner so a stray hover tooltip
    /// (e.g. the title bar's "Close" caption tip) can't bleed into a capture. The
    /// visual-enforcement specs don't care — they assert on coarse pixel stats —
    /// but the curated website screenshots (<see cref="WebsiteScreenshots"/>) are
    /// shipped images, so they move the pointer out of the frame first to keep the
    /// shot clean and deterministic.
    /// </summary>
    public static void ParkCursor() => SetCursorPos(0, 0);

    /// <summary>
    /// Capture a fixed screen rect to a 32bpp bitmap. Capturing by <em>rect</em>
    /// (not HWND) is what lets a spec re-capture the same patch of screen after
    /// its window is gone — the before/after that proves the window was really
    /// there. The caller owns (and must dispose) the returned <see cref="Bitmap"/>.
    /// </summary>
    public static Bitmap CaptureRect(ScreenRect rect)
    {
        var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        var destDc = g.GetHdc();
        var screenDc = GetDC(IntPtr.Zero);
        try
        {
            if (!BitBlt(destDc, 0, 0, rect.Width, rect.Height, screenDc, rect.Left, rect.Top, SRCCOPY | CAPTUREBLT))
            {
                bmp.Dispose();
                throw new InvalidOperationException("BitBlt failed capturing the screen rect.");
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
            g.ReleaseHdc(destDc);
        }
        return bmp;
    }

    /// <summary>
    /// Fraction (0..1) of sampled pixels that differ between two equal-size
    /// captures by more than <paramref name="perChannelThreshold"/> on any
    /// channel. The lever specs use to prove a surface was actually on screen:
    /// if a capture only ever grabbed the static background, a second capture of
    /// the same rect matches it (fraction ~0) and the assertion fails — so the
    /// test can't be fooled by whatever happens to sit behind the window.
    /// </summary>
    public static double FractionDifferent(Bitmap a, Bitmap b, int perChannelThreshold = 24, int gridStep = 4)
    {
        if (a.Width != b.Width || a.Height != b.Height)
            throw new ArgumentException($"Bitmaps differ in size ({a.Width}x{a.Height} vs {b.Width}x{b.Height}).");

        int sampled = 0, different = 0;
        for (var y = 0; y < a.Height; y += gridStep)
        {
            for (var x = 0; x < a.Width; x += gridStep)
            {
                var pa = a.GetPixel(x, y);
                var pb = b.GetPixel(x, y);
                sampled++;
                if (Math.Abs(pa.R - pb.R) > perChannelThreshold ||
                    Math.Abs(pa.G - pb.G) > perChannelThreshold ||
                    Math.Abs(pa.B - pb.B) > perChannelThreshold)
                {
                    different++;
                }
            }
        }
        return sampled == 0 ? 0 : (double)different / sampled;
    }

    /// <summary>
    /// Count distinct (coarsely-quantized) colours over a sampled grid. A WinUI
    /// surface that actually rendered carries real visual variety — background,
    /// title, the blocked-app line, the allowed-app list — so it yields many
    /// distinct colours; a failed/blank render is a single flat fill and yields
    /// ~1. This is the "the surface is not blank" lever the specs assert on.
    /// </summary>
    public static int DistinctColorCount(Bitmap bmp, int gridStep = 8)
    {
        var seen = new HashSet<int>();
        for (var y = 0; y < bmp.Height; y += gridStep)
        {
            for (var x = 0; x < bmp.Width; x += gridStep)
            {
                var c = bmp.GetPixel(x, y);
                // Quantize to 5 bits/channel so anti-aliasing noise doesn't
                // inflate the count, but genuine UI colours still separate.
                var key = ((c.R >> 3) << 10) | ((c.G >> 3) << 5) | (c.B >> 3);
                seen.Add(key);
            }
        }
        return seen.Count;
    }

    /// <summary>Diagnostic dump of every top-level window owned by <paramref name="pid"/>.</summary>
    /// <summary>
    /// When the desktop last had input, from anyone: the person at it, or a spec's
    /// own keys (<see cref="ActivateOnce"/>, <see cref="PressLetter"/>). Noted after
    /// a spec's last key, it tells whether someone has used the desktop since,
    /// which moves the foreground without the agent having a part in it.
    /// </summary>
    public static uint LastInputTick()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? info.dwTime : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    public static string DescribeWindows(int pid)
    {
        var sb = new StringBuilder();
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var windowPid);
            if (windowPid != (uint)pid) return true;

            var cls = new StringBuilder(256);
            GetClassNameW(hWnd, cls, cls.Capacity);
            GetWindowRect(hWnd, out var r);
            sb.Append("  hwnd=0x").Append(hWnd.ToInt64().ToString("X"))
              .Append(" rect=").Append(r.Left).Append(',').Append(r.Top)
              .Append(' ').Append(r.Right - r.Left).Append('x').Append(r.Bottom - r.Top)
              .Append(IsWindowVisible(hWnd) ? " visible" : " hidden")
              .Append(" class=\"").Append(cls).Append("\"\n");
            return true;
        }, IntPtr.Zero);
        return sb.Length == 0 ? "  <none>\n" : sb.ToString();
    }

    private const uint WM_CLOSE = 0x0010;
    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;
    private const int SW_RESTORE = 9;
    private const int SW_MINIMIZE = 6;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_SHOWMINNOACTIVE = 7;
    private const uint GW_OWNER = 4;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new(-4);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
                                      IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("user32.dll")]
    private static extern IntPtr SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", EntryPoint = "IsWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowNative(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "IsIconic")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconicNative(IntPtr hWnd);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x00000008;

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private const uint PM_NOREMOVE = 0x0000;
    private const byte VK_NONAME = 0xFC;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMsg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out NativeMsg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    private const uint WM_QUIT = 0x0012;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod, WinEventProc proc, uint idProcess, uint idThread, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out NativeMsg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref NativeMsg lpMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfo lpmi);
}
