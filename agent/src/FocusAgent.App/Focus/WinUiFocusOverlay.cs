using System.Collections.Generic;
using System.Runtime.Versioning;
using FocusAgent.Core.Focus;
using FocusAgent.Native.Win32;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using WinRT.Interop;

namespace FocusAgent.App.Focus;

/// <summary>
/// WinUI 3 host of the focus-enforcement overlay (issue #33). Lives across
/// the session's lifetime — Show/Hide flip <c>HWND_TOPMOST</c> via the
/// native helper; Close clears <c>HWND_NOTOPMOST</c> before tearing the
/// window down so the OS doesn't leave a phantom topmost handle.
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class WinUiFocusOverlay : IFocusOverlay, IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly IAppIdentifier _launcher;
    private readonly ILogger<WinUiFocusOverlay> _log;
    private readonly object _gate = new();
    private FocusOverlayWindow? _window;
    private bool _disposed;

    public WinUiFocusOverlay(
        DispatcherQueue dispatcher,
        IAppIdentifier launcher,
        ILogger<WinUiFocusOverlay>? log = null)
    {
        _dispatcher = dispatcher;
        _launcher = launcher;
        _log = log ?? NullLogger<WinUiFocusOverlay>.Instance;
    }

    public void Show(IReadOnlyList<AllowedAppRule> allowedRules, string? blockedAppName)
    {
        if (_disposed) return;
        _dispatcher.TryEnqueue(() => ShowOnUiThread(allowedRules, blockedAppName));
    }

    public void Hide()
    {
        if (_disposed) return;
        _dispatcher.TryEnqueue(HideOnUiThread);
    }

    public void Close()
    {
        if (_disposed) return;
        _dispatcher.TryEnqueue(CloseOnUiThread);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { CloseOnUiThread(); } catch { /* shutdown best-effort */ }
    }

    private void ShowOnUiThread(IReadOnlyList<AllowedAppRule> allowedRules, string? blockedAppName)
    {
        try
        {
            FocusOverlayWindow window;
            lock (_gate)
            {
                window = _window ??= CreateWindow();
            }

            window.UpdateContent(allowedRules, blockedAppName);
            ApplyTopmostAndPosition(window);
            window.Activate();
            _log.LogInformation("Focus overlay shown with {RuleCount} allowed-app entries", allowedRules.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Focus overlay Show failed");
        }
    }

    private void HideOnUiThread()
    {
        try
        {
            FocusOverlayWindow? window;
            lock (_gate) window = _window;
            if (window is null) return;

            var hwnd = WindowNative.GetWindowHandle(window);
            var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            appWindow.Hide();
            _log.LogDebug("Focus overlay hidden");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Focus overlay Hide failed");
        }
    }

    private void CloseOnUiThread()
    {
        FocusOverlayWindow? window;
        lock (_gate)
        {
            window = _window;
            _window = null;
        }
        if (window is null) return;
        try
        {
            var hwnd = WindowNative.GetWindowHandle(window);

            // Leave the FullScreen presenter BEFORE closing. The shell's
            // fullscreen handling can otherwise hold the window so Close() stalls
            // and the HWND survives until the process exits (seen when another
            // agent surface had been shown earlier on the same desktop) — i.e. the
            // overlay only "vanishes" on process death, not on the close path.
            // Dropping back to the default presenter releases that hold so Close()
            // tears the window down deterministically.
            ExitFullScreen(hwnd);

            // Clear HWND_TOPMOST before destroying so the OS doesn't leave a
            // phantom topmost slot in its Z-order (per #33 AC).
            ClearTopmost(hwnd);
            window.Close();
            _log.LogDebug("Focus overlay closed");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Focus overlay Close failed");
        }
    }

    private void ExitFullScreen(nint hwnd)
    {
        try
        {
            var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            if (appWindow?.Presenter?.Kind == AppWindowPresenterKind.FullScreen)
                appWindow.SetPresenter(AppWindowPresenterKind.Default);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Focus overlay presenter reset before close failed");
        }
    }

    private FocusOverlayWindow CreateWindow()
    {
        var window = new FocusOverlayWindow(_launcher, _ => HideOnUiThread());
        window.Closed += (_, _) =>
        {
            lock (_gate)
            {
                if (ReferenceEquals(_window, window))
                    _window = null;
            }
        };
        return window;
    }

    private static void ApplyTopmostAndPosition(FocusOverlayWindow window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        // Fully cover the monitor the overlay lands on — taskbar included — so a
        // student can't snap another window beside it or reach a taskbar entry
        // behind it (#103). The FullScreen presenter triggers the shell's
        // fullscreen handling, which gives true edge-to-edge coverage of the
        // current monitor; an ordinary topmost window merely sized to the
        // monitor does NOT cover the Win11 taskbar (the shell keeps it drawn on
        // top). Multi-monitor: it covers the monitor the overlay is placed on;
        // chasing the offending window onto a second monitor is deferred per the
        // issue's scope note.
        appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);

        // Belt-and-braces topmost: the FullScreen presenter already raises the
        // window, but #33 makes the explicit HWND_TOPMOST call the contract (and
        // CloseOnUiThread clears it on teardown), so keep it regardless of how
        // the presenter maps internally.
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    private static void ClearTopmost(nint hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_NOTOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }
}
