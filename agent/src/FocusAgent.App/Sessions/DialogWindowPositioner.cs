using System.Runtime.Versioning;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace FocusAgent.App.Sessions;

/// <summary>
/// Sizes an interactive dialog <see cref="Window"/> (the join-by-code prompt)
/// to a compact, fixed box centred on the primary monitor's work area, then
/// shows it WITH focus — unlike <see cref="ToastWindowPositioner"/>, which
/// pins a non-focus-stealing toast to the corner. The student is deliberately
/// typing here, so the dialog should take focus and the keyboard.
///
/// Height is measured from the content after the XAML island realises (same
/// approach as the toast, per #50) so the redesigned ink layout never clips its
/// JOIN button or wraps the error line off the bottom.
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
internal static class DialogWindowPositioner
{
    private const int DialogWidthDip = 380;

    // Roomy initial height so the first Activate has space for XAML measure to
    // complete without clipping; we then shrink to the measured height.
    private const int InitialHeightDip = 360;

    // Floor/ceiling so a not-yet-laid-out or runaway measurement can't produce
    // a sliver or a full-screen dialog.
    private const int MinHeightDip = 220;
    private const int MaxHeightDip = 420;

    public static void ConfigureAndShow(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: true);
        }

        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        var scale = dpi / 96.0;

        var width = (int)(DialogWidthDip * scale);
        var initialHeight = (int)(InitialHeightDip * scale);

        // Activate FIRST so WinUI realises the XAML island, THEN measure + size +
        // centre. Activating an interactive dialog keeps focus (we want it).
        window.Activate();

        var height = initialHeight;
        if (window.Content is FrameworkElement root)
        {
            root.Measure(new Size(DialogWidthDip, double.PositiveInfinity));
            var contentHeightDip = root.DesiredSize.Height;
            if (contentHeightDip > 0)
            {
                // The 1-DIP border + the title bar add chrome above the content;
                // add a little slack so descenders aren't shaved.
                var measured = (int)Math.Ceiling((contentHeightDip + 48) * scale);
                var floor = (int)(MinHeightDip * scale);
                var ceiling = (int)(MaxHeightDip * scale);
                if (measured < floor) measured = floor;
                if (measured > ceiling) measured = ceiling;
                height = measured;
            }
        }

        var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        var x = workArea.X + (workArea.Width - width) / 2;
        var y = workArea.Y + (workArea.Height - height) / 2;
        appWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
