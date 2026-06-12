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

    /// <summary>True while <paramref name="hwnd"/> is still a valid window handle.</summary>
    public static bool IsWindow(IntPtr hwnd) => IsWindowNative(hwnd);

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

    /// <summary>Capture the window's current on-screen rect.</summary>
    public static Bitmap Capture(IntPtr hwnd) => CaptureRect(GetWindowScreenRect(hwnd));

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
              .Append(" class=\"").Append(cls).Append("\"\n");
            return true;
        }, IntPtr.Zero);
        return sb.Length == 0 ? "  <none>\n" : sb.ToString();
    }

    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;
    private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new(-4);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
                                      IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("user32.dll")]
    private static extern IntPtr SetProcessDpiAwarenessContext(IntPtr value);

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
}
