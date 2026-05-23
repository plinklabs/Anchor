using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace FocusAgent.App.Sessions;

/// <summary>
/// Sizes a <see cref="Window"/> as a compact non-focus-stealing toast pinned
/// to the top-right of the primary monitor's work area. The window is shown
/// with <c>SW_SHOWNOACTIVATE</c> so it does not pull focus away from whatever
/// the student is currently doing (per issue #31 acceptance criteria).
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
internal static class ToastWindowPositioner
{
    private const int SW_SHOWNOACTIVATE = 4;

    private const int ToastWidthDip = 380;
    private const int ToastHeightDip = 160;
    private const int ToastMarginDip = 24;

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
        var height = (int)(ToastHeightDip * scale);
        var margin = (int)(ToastMarginDip * scale);

        var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        var x = workArea.X + workArea.Width - width - margin;
        var y = workArea.Y + margin;

        appWindow.MoveAndResize(new RectInt32(x, y, width, height));
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);
}
