using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text;
using FocusAgent.Core.Focus;
using FocusAgent.Native.Win32;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusAgent.Native;

/// <summary>
/// Minimizes off-list windows and leaves the student in the most recent allowed
/// window. A blocked window is minimized by its own thread (<c>ShowWindowAsync</c>,
/// #438): that thread owns the foreground, so Windows hands the foreground on to
/// the window below it, the app the student came from, as when they click the
/// minimize button. The agent has no right to the foreground itself:
/// <c>SetForegroundWindow</c> from it is refused, even with <c>AttachThreadInput</c>,
/// and a <c>ShowWindow</c> from it leaves the minimized window the foreground
/// window, which keeps getting what the student types. An app that is not
/// responding holds up neither way for more than a moment (#440): its window goes
/// down the moment the app responds. Nor does a minimized fallback whose app is
/// not responding (#451): it comes back the moment its app responds. With no
/// allowed window to fall back to, or for a paused browser (#450), the focus
/// overlay is put directly below the window before it minimizes itself, so the
/// overlay is the window Windows hands the foreground to (#441).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FocusEnforcer : IFocusEnforcer
{
    /// <summary>
    /// How long a window gets to go down, whether it minimizes itself or the agent
    /// minimizes it, or a minimized fallback gets to come back, before the agent
    /// logs that its app is not responding. An app that responds does it within
    /// about 100 ms. It is also how long <see cref="HandOverAsync"/> waits for the
    /// overlay to get the foreground.
    /// </summary>
    internal static readonly TimeSpan OwnThreadMinimizeTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long the agent waits for its own <c>ShowWindow</c> on another app's
    /// window before it carries on (#440, #451). An app that responds has handled
    /// it well within this, so the caller finds the window as it asked, as when the
    /// call ran on the caller's thread: <see cref="Minimize"/>'s caller shows the
    /// overlay next, which must come up after the window went down, or the
    /// foreground moves on to another window; and <see cref="Block"/>'s minimized
    /// fallback must be back on screen before the blocked window goes down, or
    /// Windows has no fallback to hand the foreground to (#438). An app that is not
    /// responding holds the caller up no longer than this.
    /// </summary>
    internal static readonly TimeSpan AgentShowWindowWait = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How soon after <see cref="HandOverAsync"/> restored a minimized window to
    /// take the foreground over from it, it does not restore that window again.
    /// Restoring it raises MINIMIZEEND, which blocks the window once more: should
    /// the overlay not get the foreground, the two would otherwise keep each other
    /// going, and the off-list app would flash on screen without end.
    /// </summary>
    internal static readonly TimeSpan RestoreToHandOverCooldown = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan HandOverPollInterval = TimeSpan.FromMilliseconds(15);

    private readonly ILogger<FocusEnforcer> _log;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<nint, long> _pendingMinimizes = new();
    private readonly ConcurrentDictionary<(nint Window, int Command), long> _showingFromAgent = new();
    private long _minimizeGeneration;
    private nint _lastAllowed;
    private nint _lastRestoredToHandOver;
    private long _lastRestoredToHandOverAt;

    public FocusEnforcer(ILogger<FocusEnforcer>? log = null)
    {
        _log = log ?? NullLogger<FocusEnforcer>.Instance;
    }

    /// <summary>
    /// Windows of explorer's own shell, which explorer owns but which are no app a
    /// student works in (#437): the taskbars, the flyouts the taskbar opens (its
    /// thumbnail previews, the hidden-icons overflow), the task switcher and the
    /// desktop. Explorer is allowed in every session, so a click on the taskbar
    /// used to make one of these the window to hand focus back to after a block:
    /// the student landed on the taskbar or desktop instead of their app, and
    /// re-showing the closing thumbnail flyout crashed explorer. File Explorer's
    /// own windows (<c>CabinetWClass</c>) are not in here: they are an app.
    /// </summary>
    private static readonly HashSet<string> ShellWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd",                        // primary taskbar
        "Shell_SecondaryTrayWnd",               // taskbar on another monitor
        "XamlExplorerHostIslandWindow",         // Windows 11 taskbar flyouts (thumbnails), Alt+Tab, Task View
        "TaskListThumbnailWnd",                 // Windows 10 taskbar thumbnails
        "TopLevelWindowForOverflowXamlIsland",  // Windows 11 hidden-icons overflow
        "NotifyIconOverflowWindow",             // Windows 10 hidden-icons overflow
        "MultitaskingViewFrame",                // Windows 10 Alt+Tab / Task View
        "ForegroundStaging",                    // explorer's stand-in while it switches windows
        "Progman",                              // desktop
        "WorkerW",                              // desktop
    };

    /// <summary>
    /// Remembers <paramref name="windowHandle"/> as the window to hand focus back
    /// to after a block, unless it is no window to return a student to (#437): one
    /// of explorer's shell windows, or a window that is already gone from the
    /// screen. Those leave the previous fallback in place.
    /// </summary>
    public void RememberAllowed(nint windowHandle)
    {
        if (windowHandle == IntPtr.Zero) return;

        var className = ClassNameOf(windowHandle);
        if (!IsFallbackCandidate(className, visible: NativeMethods.IsWindowVisible(windowHandle)))
        {
            _log.LogDebug(
                "Not remembering 0x{Hwnd:X} [{ClassName}] as the focus fallback: a shell or hidden window",
                windowHandle, className);
            return;
        }

        lock (_gate)
            _lastAllowed = windowHandle;
    }

    /// <summary>
    /// Whether an allowed window may become the focus fallback, factored out as a
    /// pure decision so it is unit-testable without a live desktop. A hidden
    /// window is no place to return a student to, and neither is explorer's shell
    /// (<see cref="ShellWindowClasses"/>). Deliberately not the full alt-tab test:
    /// owned windows (an allowed app's dialog or file picker, #428) and tool-styled
    /// windows of an allowed app are where the student was working, so they stay
    /// valid.
    /// </summary>
    internal static bool IsFallbackCandidate(string? className, bool visible)
    {
        if (!visible) return false;
        return className is null || !ShellWindowClasses.Contains(className);
    }

    private static string? ClassNameOf(nint windowHandle)
    {
        var buffer = new StringBuilder(256);
        var length = NativeMethods.GetClassNameW(windowHandle, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString(0, length) : null;
    }

    /// <summary>
    /// Minimizes <paramref name="windowHandle"/>, taking a dialog down with the app
    /// window that owns it (#428). The file picker Edge opens for an upload is
    /// owned by the Edge window even though <c>PickerHost</c> shows it: minimizing
    /// the owner hides the picker too, and brings it back when the app comes back.
    /// Minimizing the dialog on its own would leave the off-list app on screen and
    /// strand the dialog as a title bar in a corner.
    /// </summary>
    public void Minimize(nint windowHandle) => Minimize(windowHandle, onOwnThread: false);

    private void Minimize(nint windowHandle, bool onOwnThread)
    {
        if (windowHandle == IntPtr.Zero) return;

        var root = AppWindowOf(windowHandle);
        var owner = root == windowHandle ? IntPtr.Zero : root;
        var (minimizeOwner, minimizeWindow) = PlanMinimize(
            hasVisibleOwner: owner != IntPtr.Zero && NativeMethods.IsWindowVisible(owner),
            ownerMinimized: owner != IntPtr.Zero && NativeMethods.IsIconic(owner),
            windowShown: NativeMethods.IsWindowVisible(windowHandle) && !NativeMethods.IsIconic(windowHandle));
        if (minimizeOwner)
            MinimizeWindow(owner, onOwnThread);
        if (minimizeWindow)
            MinimizeWindow(windowHandle, onOwnThread);
    }

    /// <summary>
    /// Which windows <see cref="Minimize"/> takes down, factored out as a pure
    /// decision so it is unit-testable without a live desktop:
    /// <list type="bullet">
    /// <item>A window with no shown owner (an app's own top-level window, or a
    /// dialog of a hidden window) is minimized itself, as before #428. A hidden
    /// owner is never touched: minimizing it would put it on screen.</item>
    /// <item>A dialog whose owner is on screen: the owner is minimized, and its
    /// dialogs go down with it.</item>
    /// <item>A dialog whose owner is already minimized hides nothing more by
    /// minimizing the owner, so a dialog that came up on its own is minimized
    /// itself — or left alone when it is already out of sight.</item>
    /// </list>
    /// </summary>
    internal static (bool Owner, bool Window) PlanMinimize(bool hasVisibleOwner, bool ownerMinimized, bool windowShown)
    {
        if (!hasVisibleOwner) return (false, true);
        if (!ownerMinimized) return (true, false);
        return (false, windowShown);
    }

    /// <summary>
    /// The app window <paramref name="windowHandle"/> belongs to: the top-level
    /// window at the root of its owner chain, or the window itself when it has no
    /// owner.
    /// </summary>
    private static nint AppWindowOf(nint windowHandle)
    {
        var root = NativeMethods.GetAncestor(windowHandle, NativeMethods.GA_ROOTOWNER);
        return root == IntPtr.Zero ? windowHandle : root;
    }

    /// <summary>
    /// Minimizes one window: from the agent (<see cref="MinimizeFromAgent"/>), or by
    /// posting the request to the window's thread (<paramref name="onOwnThread"/>,
    /// #438). The posted request never holds up the agent, and the window's thread
    /// carries it out with the foreground rights it has.
    /// </summary>
    private void MinimizeWindow(nint windowHandle, bool onOwnThread)
    {
        try
        {
            if (!onOwnThread)
            {
                MinimizeFromAgent(windowHandle);
                return;
            }
            if (!NativeMethods.ShowWindowAsync(windowHandle, NativeMethods.SW_MINIMIZE))
            {
                _log.LogWarning("ShowWindowAsync(SW_MINIMIZE) returned false for 0x{Hwnd:X}", windowHandle);
                return;
            }
            _ = WarnIfNotMinimizedAsync(windowHandle);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Minimizing threw for 0x{Hwnd:X}", windowHandle);
        }
    }

    /// <summary>
    /// Minimizes one window with the agent's own <c>ShowWindow</c>
    /// (<see cref="ShowFromAgent"/>, #440), for the session-start sweep and for
    /// paused browser windows that do not have the foreground. A blocked or paused
    /// window with the foreground goes to the overlay instead
    /// (<see cref="HandOverAsync"/>, #441, #450): minimized from the agent, it either
    /// stays the foreground window and keeps what the student types, or hands the
    /// foreground to the window below it, which may be an allowed app that hides the
    /// overlay (Windows 11 Notepad does the latter).
    /// </summary>
    private void MinimizeFromAgent(nint windowHandle) => ShowFromAgent(windowHandle, NativeMethods.SW_MINIMIZE);

    /// <summary>
    /// Carries out the agent's own <c>ShowWindow</c> on another app's window on a
    /// thread of its own, and waits for it no longer than
    /// <see cref="AgentShowWindowWait"/>: <see cref="MinimizeFromAgent"/> (#440), and
    /// <see cref="Block"/> restoring a minimized fallback (#451). <c>ShowWindow</c> on
    /// another thread's window waits until that thread handles messages again. Run
    /// on the caller's thread, an app that was not responding held up the agent's
    /// UI thread, and every foreground change behind it, until the app responded,
    /// and its window changed no sooner. Now the window changes the moment its app
    /// responds. The same request for a window whose earlier one has not been
    /// carried out yet adds nothing, so it is dropped without waiting.
    /// </summary>
    private void ShowFromAgent(nint windowHandle, int command)
    {
        var key = (windowHandle, command);
        var request = Interlocked.Increment(ref _minimizeGeneration);
        if (!_showingFromAgent.TryAdd(key, request))
        {
            _log.LogDebug("ShowWindow({Command}) for 0x{Hwnd:X} is still on its way", CommandName(command), windowHandle);
            return;
        }
        // A thread of its own rather than the thread pool: a ShowWindow that waits
        // for an app that is not responding ties its thread up for as long as the
        // app hangs.
        var showing = Task.Factory.StartNew(() =>
        {
            try
            {
                if (!NativeMethods.ShowWindow(windowHandle, command))
                    _log.LogWarning("ShowWindow({Command}) returned false for 0x{Hwnd:X}", CommandName(command), windowHandle);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "ShowWindow({Command}) threw for 0x{Hwnd:X}", CommandName(command), windowHandle);
            }
            finally
            {
                _showingFromAgent.TryRemove(new KeyValuePair<(nint, int), long>(key, request));
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _ = WarnIfStillShowingAsync(key, request);
        showing.Wait(AgentShowWindowWait);
    }

    private static string CommandName(int command) => command switch
    {
        NativeMethods.SW_MINIMIZE => "SW_MINIMIZE",
        NativeMethods.SW_SHOWNOACTIVATE => "SW_SHOWNOACTIVATE",
        _ => command.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Logs that the app of a window the agent is minimizing or restoring is not
    /// responding, when its <c>ShowWindow</c> has not returned
    /// <see cref="OwnThreadMinimizeTimeout"/> later. Not judged by <c>IsIconic</c>:
    /// Windows marks the window minimized before its app has handled the request,
    /// while the window is still on screen.
    /// </summary>
    private async Task WarnIfStillShowingAsync((nint Window, int Command) key, long request)
    {
        await Task.Delay(OwnThreadMinimizeTimeout).ConfigureAwait(false);
        if (!_showingFromAgent.TryGetValue(key, out var current) || current != request)
            return;
        if (key.Command == NativeMethods.SW_MINIMIZE)
        {
            _log.LogWarning(
                "0x{Hwnd:X} [{ClassName}] did not minimize within {TimeoutMs} ms: its app is not responding. It goes down as soon as the app responds",
                key.Window, ClassNameOf(key.Window), OwnThreadMinimizeTimeout.TotalMilliseconds);
        }
        else
        {
            _log.LogWarning(
                "0x{Hwnd:X} [{ClassName}] did not come back within {TimeoutMs} ms: its app is not responding. It comes back as soon as the app responds",
                key.Window, ClassNameOf(key.Window), OwnThreadMinimizeTimeout.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Logs a window that has not minimized itself <see cref="OwnThreadMinimizeTimeout"/>
    /// after it was asked to: its app is not responding. The request stays queued and
    /// the window goes down the moment the app responds again. Minimizing it from the
    /// agent would not get it down any sooner: <c>ShowWindow</c> waits for the app too.
    /// A newer request for the same window, after the student brought it back, takes
    /// over the check.
    /// </summary>
    private async Task WarnIfNotMinimizedAsync(nint windowHandle)
    {
        var generation = Interlocked.Increment(ref _minimizeGeneration);
        _pendingMinimizes[windowHandle] = generation;
        await Task.Delay(OwnThreadMinimizeTimeout).ConfigureAwait(false);
        if (!_pendingMinimizes.TryRemove(new KeyValuePair<nint, long>(windowHandle, generation)))
            return;
        if (NativeMethods.IsWindow(windowHandle) && NativeMethods.IsWindowVisible(windowHandle) &&
            !NativeMethods.IsIconic(windowHandle))
        {
            _log.LogWarning(
                "0x{Hwnd:X} [{ClassName}] did not minimize within {TimeoutMs} ms: its app is not responding. It goes down as soon as the app responds",
                windowHandle, ClassNameOf(windowHandle), OwnThreadMinimizeTimeout.TotalMilliseconds);
        }
    }

    public bool Block(nint offendingWindowHandle)
    {
        if (offendingWindowHandle == IntPtr.Zero) return false;

        nint target;
        lock (_gate)
            target = _lastAllowed;

        var plan = target == IntPtr.Zero || !NativeMethods.IsWindow(target)
            ? FallbackPlan.None
            : PlanFallback(
                sameAppAsBlocked: AppWindowOf(target) == AppWindowOf(offendingWindowHandle),
                visible: NativeMethods.IsWindowVisible(target),
                minimized: NativeMethods.IsIconic(target));
        if (plan == FallbackPlan.None)
        {
            // The caller surfaces the overlay, which takes the window down with
            // HandOverAsync (#441). Minimized from here, the blocked window would
            // keep the foreground and what the student types; minimized by itself
            // before the overlay is below it, it would hand the foreground to the
            // desktop or another allowed window, which hides the overlay.
            _log.LogDebug("Block: no valid prior allowed window to restore; leaving the window to the overlay");
            return false;
        }

        if (plan == FallbackPlan.Restore)
        {
            // Back on screen before the blocked window goes down, so Windows can
            // hand it the foreground. A fallback whose app is not responding holds
            // the block up no longer than a moment (#451): the blocked window goes
            // down all the same, and the fallback comes back as soon as its app
            // responds.
            ShowFromAgent(target, NativeMethods.SW_SHOWNOACTIVATE);
        }
        Minimize(offendingWindowHandle, onOwnThread: true);
        return true;
    }

    /// <summary>What <see cref="Block"/> does with the remembered allowed window.</summary>
    internal enum FallbackPlan
    {
        /// <summary>Nothing to fall back to: the caller surfaces the overlay.</summary>
        None,
        /// <summary>The window is on screen: leave it as it is.</summary>
        OnScreen,
        /// <summary>The window is minimized: restore it, without activating it.</summary>
        Restore,
    }

    /// <summary>
    /// What <see cref="Block"/> does with the remembered allowed window, factored
    /// out as a pure decision so it is unit-testable without a live desktop. The
    /// window is not given the foreground: the blocked window minimizes itself and
    /// Windows hands the foreground to the window below it (#438).
    /// <list type="bullet">
    /// <item>Never the app just taken down: the blocked window itself, the app
    /// window that owns a blocked dialog, or a dialog of the blocked app (#428).</item>
    /// <item>Never a window that has gone from the screen since it was
    /// remembered: showing it again would put back a window its app hid. The
    /// taskbar's thumbnail flyout re-shown that way crashed explorer (#437).</item>
    /// <item>A minimized window is restored; a window already on screen is left
    /// as it is.</item>
    /// </list>
    /// </summary>
    internal static FallbackPlan PlanFallback(bool sameAppAsBlocked, bool visible, bool minimized)
    {
        if (sameAppAsBlocked || !visible) return FallbackPlan.None;
        return minimized ? FallbackPlan.Restore : FallbackPlan.OnScreen;
    }

    public async Task<bool> HandOverAsync(nint offendingWindowHandle, nint successorWindowHandle)
    {
        if (offendingWindowHandle == IntPtr.Zero) return false;
        if (successorWindowHandle == IntPtr.Zero || !NativeMethods.IsWindow(successorWindowHandle))
        {
            // Nothing to hand over to: the window goes down all the same.
            Minimize(offendingWindowHandle);
            return false;
        }

        if (NativeMethods.GetForegroundWindow() == successorWindowHandle)
        {
            // The successor has the keyboard already: a second block of the same
            // window (a click raises FOREGROUND and MINIMIZEEND, and the restore below
            // raises MINIMIZEEND once more). Only make sure the window goes down. The
            // successor stays where it is: put below a minimized window, at the bottom
            // of the Z-order, it would lose the foreground to the window on top.
            Minimize(offendingWindowHandle, onOwnThread: true);
            return true;
        }

        var plan = PlanHandOver(
            minimized: NativeMethods.IsIconic(offendingWindowHandle),
            hasForeground: NativeMethods.GetForegroundWindow() == offendingWindowHandle,
            isAppWindow: AppWindowOf(offendingWindowHandle) == offendingWindowHandle);
        if (plan == HandOverPlan.RestoreFirst && !TryClaimRestoreToHandOver(offendingWindowHandle))
            plan = HandOverPlan.AlreadyDown;
        if (plan == HandOverPlan.RestoreFirst)
        {
            // Restored by its own thread, it is on screen with the foreground once
            // more, so minimizing it next hands the foreground on. The successor goes
            // below it only once it is back on top.
            _log.LogDebug("Restoring minimized 0x{Hwnd:X}, which has the foreground, to hand it over", offendingWindowHandle);
            try { NativeMethods.ShowWindowAsync(offendingWindowHandle, NativeMethods.SW_RESTORE); }
            catch (Exception ex) { _log.LogError(ex, "ShowWindowAsync(SW_RESTORE) threw for 0x{Hwnd:X}", offendingWindowHandle); }
            await WaitUntilAsync(() => !NativeMethods.IsIconic(offendingWindowHandle), OwnThreadMinimizeTimeout)
                .ConfigureAwait(false);
        }
        if (plan != HandOverPlan.AlreadyDown)
        {
            // Windows activates the window below the one that goes down. Put after a
            // window that is not topmost, the successor leaves the topmost band until
            // the caller puts it back.
            try
            {
                if (!NativeMethods.SetWindowPos(successorWindowHandle, offendingWindowHandle, 0, 0, 0, 0,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE))
                    _log.LogWarning("SetWindowPos could not put 0x{Successor:X} below 0x{Hwnd:X}", successorWindowHandle, offendingWindowHandle);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "SetWindowPos threw putting 0x{Successor:X} below 0x{Hwnd:X}", successorWindowHandle, offendingWindowHandle);
            }
            Minimize(offendingWindowHandle, onOwnThread: true);
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (await WaitUntilAsync(() => NativeMethods.GetForegroundWindow() == successorWindowHandle, OwnThreadMinimizeTimeout)
                .ConfigureAwait(false))
        {
            _log.LogDebug(
                "0x{Successor:X} took the foreground over from 0x{Hwnd:X} in {ElapsedMs} ms",
                successorWindowHandle, offendingWindowHandle, clock.ElapsedMilliseconds);
            return true;
        }
        var foreground = NativeMethods.GetForegroundWindow();
        _log.LogInformation(
            "0x{Successor:X} did not get the foreground from 0x{Hwnd:X} within {TimeoutMs} ms; the foreground is 0x{Foreground:X} [{ClassName}]",
            successorWindowHandle, offendingWindowHandle, OwnThreadMinimizeTimeout.TotalMilliseconds,
            foreground, ClassNameOf(foreground));
        return false;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (condition()) return true;
            await Task.Delay(HandOverPollInterval).ConfigureAwait(false);
        }
        return condition();
    }

    /// <summary>How <see cref="HandOverAsync"/> takes the blocked window down.</summary>
    internal enum HandOverPlan
    {
        /// <summary>It minimizes itself.</summary>
        Minimize,
        /// <summary>It is restored first, then minimizes itself.</summary>
        RestoreFirst,
        /// <summary>
        /// It is down and hands nothing on: the successor is left where it is.
        /// </summary>
        AlreadyDown,
    }

    /// <summary>
    /// How <see cref="HandOverAsync"/> takes the blocked window down, factored out
    /// as a pure decision so it is unit-testable without a live desktop. A window
    /// minimizing itself hands the foreground to the window below it, but one that
    /// is minimized already hands nothing on: an off-list app activated while
    /// minimized (the first step of a click on its taskbar button) would keep the
    /// keyboard. So that window is restored first, by its own thread, as a click
    /// on its taskbar button would. Only an app's own window: a dialog goes down
    /// with the window that owns it (#428). A minimized window without the
    /// foreground has nothing to hand on and is left as it is.
    /// </summary>
    internal static HandOverPlan PlanHandOver(bool minimized, bool hasForeground, bool isAppWindow)
    {
        if (!minimized) return HandOverPlan.Minimize;
        if (!hasForeground) return HandOverPlan.AlreadyDown;
        return isAppWindow ? HandOverPlan.RestoreFirst : HandOverPlan.Minimize;
    }

    public nint ForegroundWindowOf(nint windowHandle)
    {
        if (windowHandle == IntPtr.Zero) return IntPtr.Zero;
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return IntPtr.Zero;
        return IsWindowOrItsDialog(foreground, AppWindowOf(foreground), windowHandle) ? foreground : IntPtr.Zero;
    }

    /// <summary>
    /// Whether <paramref name="window"/>, whose app window is
    /// <paramref name="windowsAppWindow"/>, is <paramref name="appWindow"/> itself
    /// or a dialog that window owns (#428), factored out as a pure decision so it
    /// is unit-testable without a live desktop.
    /// </summary>
    internal static bool IsWindowOrItsDialog(nint window, nint windowsAppWindow, nint appWindow) =>
        window == appWindow || windowsAppWindow == appWindow;

    /// <summary>
    /// Whether <see cref="HandOverAsync"/> may restore <paramref name="windowHandle"/>
    /// now: not when it did so for that window within <see cref="RestoreToHandOverCooldown"/>.
    /// </summary>
    private bool TryClaimRestoreToHandOver(nint windowHandle)
    {
        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (_lastRestoredToHandOver == windowHandle &&
                now - _lastRestoredToHandOverAt < (long)RestoreToHandOverCooldown.TotalMilliseconds)
            {
                _log.LogDebug("Not restoring 0x{Hwnd:X} again so soon to hand it over", windowHandle);
                return false;
            }
            _lastRestoredToHandOver = windowHandle;
            _lastRestoredToHandOverAt = now;
            return true;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _lastAllowed = IntPtr.Zero;
            _lastRestoredToHandOver = IntPtr.Zero;
        }
    }
}
