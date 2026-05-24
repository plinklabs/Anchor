using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace FocusAgent.App.Sessions;

/// <summary>
/// Sizes a <see cref="Window"/> as a compact toast pinned to the top-right of
/// the primary monitor's work area, then shows it without stealing focus from
/// whatever the student is currently doing (per #31 acceptance criteria).
///
/// The XAML island that renders the window's content is only initialised on
/// <see cref="Window.Activate"/>; earlier versions of this file used raw
/// <c>ShowWindow(SW_SHOWNOACTIVATE)</c>, which made the HWND visible but
/// skipped the WinUI show path entirely — the XAML stayed unrendered, so the
/// toast never appeared. That's the root cause of #41. The fix is to
/// <see cref="Window.Activate"/> (forcing the XAML island to render) and
/// immediately restore foreground to whatever was active before, which the OS
/// allows because this thread just received focus.
///
/// Height is measured from the content after layout rather than hard-coded
/// (per #50). A fixed 160-DIP height previously clipped the Cancel button,
/// silently leaving the student no way to decline the auto-join.
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
internal static class ToastWindowPositioner
{
    private const int ToastWidthDip = 380;
    private const int ToastMarginDip = 24;

    // Generous initial height so the first Activate has enough room for XAML
    // measure to complete without clipping. We then shrink to the measured
    // height. Empirically the content lands around ~200-260 DIP depending on
    // teacher-name wrap.
    private const int InitialHeightDip = 320;

    // Floor so an unexpectedly-small measurement (e.g. content not yet laid
    // out) doesn't produce a sliver of a window.
    private const int MinHeightDip = 160;

    // Ceiling so a runaway measurement (e.g. infinity from a layout glitch)
    // doesn't produce a full-screen window. Generous enough to absorb a
    // 3-line teacher name at 144 DPI.
    private const int MaxHeightDip = 360;

    public static void ConfigureAndShow(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        }

        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        var scale = dpi / 96.0;

        var width = (int)(ToastWidthDip * scale);
        var margin = (int)(ToastMarginDip * scale);

        var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        var x = workArea.X + workArea.Width - width - margin;
        var y = workArea.Y + margin;

        // Initial roomy size so the XAML island has space to lay out its
        // content without clipping. Measured shrink happens right after.
        var initialHeight = (int)(InitialHeightDip * scale);

        // Activate FIRST so WinUI initialises the XAML island + composition
        // surface, THEN MoveAndResize so the rendered content is repositioned
        // along with the HWND. After that, restore foreground to whatever the
        // student was using — Windows allows this because this thread just
        // received focus, so the toast stays topmost but doesn't keep input.
        var originalForeground = GetForegroundWindow();
        window.Activate();
        appWindow.MoveAndResize(new RectInt32(x, y, width, initialHeight));

        // Now that the island is realised, ask the root element what height it
        // wants given the toast's fixed width — then shrink the HWND to that.
        // We use Measure/DesiredSize rather than ActualHeight because the root
        // Grid is the Window.Content and so stretches to fill the client area,
        // making ActualHeight equal to the initial window height (the #49
        // regression of the #50 fix). DesiredSize reflects only what the
        // content needs.
        if (window.Content is FrameworkElement root)
        {
            root.Measure(new Size(ToastWidthDip, double.PositiveInfinity));
            var contentHeightDip = root.DesiredSize.Height;
            if (contentHeightDip > 0)
            {
                // SetBorderAndTitleBar(hasBorder: true) adds a 1-DIP frame on
                // each edge; add 2 DIP of slack so descenders aren't shaved.
                var measuredHeight = (int)Math.Ceiling((contentHeightDip + 2) * scale);
                var floor = (int)(MinHeightDip * scale);
                var ceiling = (int)(MaxHeightDip * scale);
                if (measuredHeight < floor) measuredHeight = floor;
                if (measuredHeight > ceiling) measuredHeight = ceiling;
                if (measuredHeight != initialHeight)
                {
                    appWindow.MoveAndResize(new RectInt32(x, y, width, measuredHeight));
                }
            }
        }

        if (originalForeground != IntPtr.Zero && originalForeground != hwnd)
            SetForegroundWindow(originalForeground);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);
}
