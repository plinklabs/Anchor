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
/// blocked app (#441) or a paused browser (#450), it takes that window down and
/// the foreground over from it through the enforcer, so the student's keystrokes
/// come to the overlay. A show runs on the UI thread a moment after it is asked
/// for, behind the shows asked for before it: one for a paused browser whose
/// pause was lifted meanwhile is called off (#474), and one that comes while a
/// hand-over is under way leaves the overlay where that hand-over put it (#475).
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
    // What the overlay shows while it is up, null while it is not (see Showing).
    private string? _showing;
    // The hand-overs under way (UI thread only), and the window they are for: a
    // closed window's hand-overs say nothing about the next one's.
    private FocusOverlayWindow? _handingOver;
    private int _handOversUnderWay;

    /// <summary>
    /// What the overlay shows while it is up: <c>Blocked</c> for a blocked app
    /// (#33), <c>BrowserPaused</c> for a paused browser (#427); null while it is
    /// hidden or closed. Dev-only, for /status: lets the headless e2e tell which
    /// overlay the student is on (#452).
    /// </summary>
    public string? Showing => Volatile.Read(ref _showing);

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

    public void Show(IReadOnlyList<AllowedAppRule> allowedRules, string? blockedAppName, nint blockedWindowHandle) =>
        Show(
            window => window.UpdateContent(allowedRules, blockedAppName),
            "Blocked",
            blockedWindowHandle,
            $"with {allowedRules.Count} allowed-app entries",
            "the blocked app",
            stillWanted: null);

    public void ShowBrowserPaused(
        IReadOnlyList<AllowedAppRule> allowedRules,
        BrowserPausedPrompt prompt,
        nint pausedWindowHandle,
        Func<bool>? stillPaused = null) =>
        Show(
            window => window.UpdateBrowserPausedContent(allowedRules, prompt),
            "BrowserPaused",
            pausedWindowHandle,
            $"for a paused {prompt.Browser.Value} with {allowedRules.Count} other allowed-app entries",
            // The paused browser's window (#450), or an app blocked meanwhile (#452).
            $"0x{pausedWindowHandle:X}",
            stillPaused);

    /// <summary>
    /// Shows the overlay with the content <paramref name="update"/> puts in it
    /// (<paramref name="content"/>, for <see cref="Showing"/>), and takes
    /// <paramref name="windowHandle"/> (the blocked app, #441, the paused browser,
    /// #450, or an app blocked while the browser is paused, #452) down and the
    /// foreground over from it. The window still goes down when the overlay cannot
    /// come up. Once <paramref name="stillWanted"/> returns false, the show is
    /// called off and the window left as it is (#474).
    /// </summary>
    private void Show(
        Action<FocusOverlayWindow> update, string content, nint windowHandle, string shown, string takenFrom,
        Func<bool>? stillWanted)
    {
        if (_disposed || !_dispatcher.TryEnqueue(
                () => _ = ShowOnUiThreadAsync(update, content, windowHandle, shown, takenFrom, stillWanted)))
            TakeDownWithoutOverlay(windowHandle, stillWanted);
    }

    public void Hide()
    {
        if (_disposed) return;
        _dispatcher.TryEnqueue(HideOnUiThread);
    }

    /// <summary>
    /// Clicks the overlay's row for the allowed app <paramref name="app"/> (a
    /// rule's value) on the UI thread, where the student's click runs too.
    /// Dev-only, for the status endpoint: lets the headless e2e take the student's
    /// way back to an allowed app (#453). Returns whether the overlay was up to
    /// click; it does not wait for the click itself.
    /// </summary>
    public bool ClickAllowedApp(string app)
    {
        if (_disposed || Showing is null) return false;
        return _dispatcher.TryEnqueue(() =>
        {
            FocusOverlayWindow? window;
            lock (_gate) window = _window;
            try
            {
                _log.LogInformation("Clicking the focus overlay's row for {App}", app);
                if (window is null || !window.ClickAllowedApp(app))
                    _log.LogWarning("The focus overlay has no row for {App} to click", app);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Clicking the focus overlay's row for {App} failed", app);
            }
        });
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
        Action<FocusOverlayWindow> update, string content, nint windowHandle, string shown, string takenFrom,
        Func<bool>? stillWanted)
    {
        if (!StillWanted(stillWanted))
        {
            // Asked for while the browser was paused, which it no longer is: the
            // overlay would cover the browser the student just got back, and its
            // hand-over minimize it (#474).
            _log.LogInformation("Not showing the focus overlay {Shown}: the browser is no longer paused", shown);
            return;
        }
        var generation = ++_generation;
        try
        {
            FocusOverlayWindow window;
            lock (_gate)
            {
                window = _window ??= CreateWindow();
            }

            update(window);
            Volatile.Write(ref _showing, content);
            var handsOver = windowHandle != IntPtr.Zero && _enforcer is not null;
            var hwnd = WindowNative.GetWindowHandle(window);
            // A hand-over under way took the overlay out of the topmost band, so
            // that Windows hands it the foreground when the window goes down, which
            // the window does whenever its app gets to it. Put back on top by this
            // show, the overlay was passed over by a window that went down
            // meanwhile, and an allowed app below got the keyboard (#475). While it
            // is up, it is full screen already: leave it where it is.
            if (!handsOver || !HandOverUnderWay(window) || !NativeMethods.IsWindowVisible(hwnd))
                ApplyTopmostAndPosition(window);
            if (handsOver)
            {
                // The agent may not take the foreground, so the overlay is put
                // above every app window, out of the topmost band, and the window
                // minimizes itself and so hands the foreground to it (#441, #475).
                // It must be on screen for that.
                AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd)).Show(activateWindow: false);
                bool tookOver;
                StartHandOver(window);
                try
                {
                    tookOver = await _enforcer!.HandOverAsync(windowHandle, hwnd, stillWanted);
                }
                catch
                {
                    OnUiThread(() => EndHandOver(window));
                    throw;
                }
                OnUiThread(() =>
                {
                    EndHandOver(window);
                    FinishShow(window, generation, tookOver, shown, takenFrom, stillWanted);
                });
                return;
            }
            window.Activate();
            _log.LogInformation("Focus overlay shown {Shown}", shown);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Focus overlay Show failed");
            Volatile.Write(ref _showing, null);
            TakeDownWithoutOverlay(windowHandle, stillWanted);
        }
    }

    private void OnUiThread(Action action)
    {
        if (_dispatcher.HasThreadAccess)
            action();
        else
            _dispatcher.TryEnqueue(() => action());
    }

    private bool HandOverUnderWay(FocusOverlayWindow window) =>
        _handOversUnderWay > 0 && ReferenceEquals(_handingOver, window);

    private void StartHandOver(FocusOverlayWindow window)
    {
        if (!ReferenceEquals(_handingOver, window))
        {
            _handingOver = window;
            _handOversUnderWay = 0;
        }
        _handOversUnderWay++;
    }

    private void EndHandOver(FocusOverlayWindow window)
    {
        if (ReferenceEquals(_handingOver, window) && _handOversUnderWay > 0)
            _handOversUnderWay--;
    }

    /// <summary>
    /// After the hand-over: back on top of everything, unless a newer show, a hide
    /// or a close came meanwhile.
    /// </summary>
    private void FinishShow(
        FocusOverlayWindow window, int generation, bool tookOver, string shown, string takenFrom, Func<bool>? stillWanted)
    {
        if (generation != _generation)
            return;
        try
        {
            SetTopmost(WindowNative.GetWindowHandle(window));
            window.Activate();
            if (tookOver)
                _log.LogInformation(
                    "Focus overlay shown {Shown}; it took the keyboard over from {TakenFrom}", shown, takenFrom);
            else if (!StillWanted(stillWanted))
                _log.LogInformation(
                    "Focus overlay shown {Shown}; {TakenFrom} was left as it is: the browser is no longer paused",
                    shown, takenFrom);
            else
                _log.LogWarning(
                    "Focus overlay shown {Shown}, but it did not get the keyboard from {TakenFrom}", shown, takenFrom);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Focus overlay Show failed after the hand-over");
        }
    }

    /// <summary>
    /// The blocked or paused window still goes down when the overlay cannot come
    /// up, unless <paramref name="stillWanted"/> says it no longer has to (#474).
    /// </summary>
    private void TakeDownWithoutOverlay(nint windowHandle, Func<bool>? stillWanted)
    {
        if (windowHandle == IntPtr.Zero || _enforcer is null || !StillWanted(stillWanted)) return;
        try { _enforcer.Minimize(windowHandle); }
        catch (Exception ex) { _log.LogWarning(ex, "Minimizing the window without the overlay failed"); }
    }

    private bool StillWanted(Func<bool>? stillWanted)
    {
        if (stillWanted is null) return true;
        try { return stillWanted(); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Asking whether the focus overlay is still wanted threw; showing it");
            return true;
        }
    }

    private void HideOnUiThread()
    {
        _generation++;
        Volatile.Write(ref _showing, null);
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
        Volatile.Write(ref _showing, null);
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
