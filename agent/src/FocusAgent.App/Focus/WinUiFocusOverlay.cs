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
/// window down so the OS doesn't leave a phantom topmost handle. Shown for a
/// blocked app, it takes the app's window down and the foreground over from it
/// through the enforcer (#441), so the student's keystrokes come to the overlay.
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class WinUiFocusOverlay : IFocusOverlay, IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly IAppIdentifier _launcher;
    private readonly IFocusEnforcer? _enforcer;
    private readonly ILogger<WinUiFocusOverlay> _log;
    private readonly object _gate = new();
    private FocusOverlayWindow? _window;
    private bool _disposed;
    // Bumped on the UI thread by every show, hide and close: a hand-over that
    // finishes after a newer one leaves the overlay to that newer one.
    private int _generation;

    public WinUiFocusOverlay(
        DispatcherQueue dispatcher,
        IAppIdentifier launcher,
        ILogger<WinUiFocusOverlay>? log = null,
        IFocusEnforcer? enforcer = null)
    {
        _dispatcher = dispatcher;
        _launcher = launcher;
        _log = log ?? NullLogger<WinUiFocusOverlay>.Instance;
        _enforcer = enforcer;
    }

    public void Show(IReadOnlyList<AllowedAppRule> allowedRules, string? blockedAppName, nint blockedWindowHandle)
    {
        if (_disposed || !_dispatcher.TryEnqueue(() => _ = ShowOnUiThreadAsync(allowedRules, blockedAppName, blockedWindowHandle)))
            TakeDownWithoutOverlay(blockedWindowHandle);
    }

    public void ShowBrowserPaused(IReadOnlyList<AllowedAppRule> allowedRules, BrowserPausedPrompt prompt)
    {
        if (_disposed) return;
        _dispatcher.TryEnqueue(() => ShowBrowserPausedOnUiThread(allowedRules, prompt));
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

    private async Task ShowOnUiThreadAsync(
        IReadOnlyList<AllowedAppRule> allowedRules, string? blockedAppName, nint blockedWindowHandle)
    {
        var generation = ++_generation;
        try
        {
            FocusOverlayWindow window;
            lock (_gate)
            {
                window = _window ??= CreateWindow();
            }

            window.UpdateContent(allowedRules, blockedAppName);
            ApplyTopmostAndPosition(window);
            if (blockedWindowHandle != IntPtr.Zero && _enforcer is not null)
            {
                // The agent may not take the foreground, so the overlay is put
                // below the blocked window, which minimizes itself and so hands the
                // foreground to it (#441). It must be on screen for that.
                var hwnd = WindowNative.GetWindowHandle(window);
                AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd)).Show(activateWindow: false);
                var tookOver = await _enforcer.HandOverAsync(blockedWindowHandle, hwnd);
                if (!_dispatcher.HasThreadAccess)
                {
                    _dispatcher.TryEnqueue(() => FinishShow(window, generation, tookOver, allowedRules.Count));
                    return;
                }
                FinishShow(window, generation, tookOver, allowedRules.Count);
                return;
            }
            window.Activate();
            _log.LogInformation("Focus overlay shown with {RuleCount} allowed-app entries", allowedRules.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Focus overlay Show failed");
            TakeDownWithoutOverlay(blockedWindowHandle);
        }
    }

    /// <summary>
    /// After the hand-over: back on top of everything, unless a newer show, a hide
    /// or a close came meanwhile.
    /// </summary>
    private void FinishShow(FocusOverlayWindow window, int generation, bool tookOver, int ruleCount)
    {
        if (generation != _generation)
            return;
        try
        {
            SetTopmost(WindowNative.GetWindowHandle(window));
            window.Activate();
            if (tookOver)
                _log.LogInformation(
                    "Focus overlay shown with {RuleCount} allowed-app entries; it took the keyboard over from the blocked app",
                    ruleCount);
            else
                _log.LogWarning(
                    "Focus overlay shown with {RuleCount} allowed-app entries, but it did not get the keyboard from the blocked app",
                    ruleCount);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Focus overlay Show failed after the hand-over");
        }
    }

    /// <summary>The blocked window still goes down when the overlay cannot come up.</summary>
    private void TakeDownWithoutOverlay(nint blockedWindowHandle)
    {
        if (blockedWindowHandle == IntPtr.Zero || _enforcer is null) return;
        try { _enforcer.Minimize(blockedWindowHandle); }
        catch (Exception ex) { _log.LogWarning(ex, "Minimizing the blocked window without the overlay failed"); }
    }

    private void ShowBrowserPausedOnUiThread(IReadOnlyList<AllowedAppRule> allowedRules, BrowserPausedPrompt prompt)
    {
        try
        {
            FocusOverlayWindow window;
            lock (_gate)
            {
                window = _window ??= CreateWindow();
            }

            window.UpdateBrowserPausedContent(allowedRules, prompt);
            ApplyTopmostAndPosition(window);
            window.Activate();
            _log.LogInformation(
                "Focus overlay shown for a paused {Browser} with {RuleCount} other allowed-app entries",
                prompt.Browser.Value, allowedRules.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Focus overlay ShowBrowserPaused failed");
        }
    }

    private void HideOnUiThread()
    {
        _generation++;
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
        _generation++;
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
        SetTopmost(hwnd);
    }

    private static void SetTopmost(nint hwnd) =>
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

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
