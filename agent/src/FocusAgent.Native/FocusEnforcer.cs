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
/// not responding (#451), or an allowed app the student picks on the overlay
/// (#453): it comes back the moment its app responds. With no
/// allowed window to fall back to, or for a paused browser (#450), the focus
/// overlay is put above every app window, out of the topmost band, before the
/// window minimizes itself, so the overlay is the window Windows hands the
/// foreground to (#441, #475). A hand-over never
/// asks a window to minimize while an earlier minimize is on its way to it, nor
/// once its caller no longer wants the window down (#474).
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
    /// Windows has no fallback to hand the foreground to (#438); and
    /// <see cref="BringForward"/> gives the foreground to a window that is back on
    /// screen (#453). An app that is not responding holds the caller up no longer
    /// than this.
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

    /// <summary>
    /// How long a minimize a hand-over asked of a window counts as on its way when
    /// the window does not go down (#474): an app that is not responding. A
    /// hand-over after that asks again.
    /// </summary>
    internal static readonly TimeSpan MinimizeOnItsWayLimit = TimeSpan.FromSeconds(2);

    private readonly ILogger<FocusEnforcer> _log;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<nint, long> _pendingMinimizes = new();
    private readonly ConcurrentDictionary<(nint Window, int Command), long> _showingFromAgent = new();
    private long _minimizeGeneration;
    private nint _lastAllowed;
    private nint _lastRestoredToHandOver;
    private long _lastRestoredToHandOverAt;
    // The windows a hand-over asked to minimize that have not started to go down
    // yet, with when it asked (Environment.TickCount64, #474). Guarded by _gate.
    private readonly Dictionary<nint, long> _minimizeOnItsWay = new();
    private readonly MinimizeStartWatcher _minimizeStarts;

    public FocusEnforcer(ILogger<FocusEnforcer>? log = null)
    {
        _log = log ?? NullLogger<FocusEnforcer>.Instance;
        _minimizeStarts = new MinimizeStartWatcher(OnMinimizeStart, _log);
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
    /// screen. Those leave the previous fallback in place, and return false.
    /// </summary>
    public bool RememberAllowed(nint windowHandle)
    {
        if (windowHandle == IntPtr.Zero) return false;

        var className = ClassNameOf(windowHandle);
        if (!IsFallbackCandidate(className, visible: NativeMethods.IsWindowVisible(windowHandle)))
        {
            _log.LogDebug(
                "Not remembering 0x{Hwnd:X} [{ClassName}] as the focus fallback: a shell or hidden window",
                windowHandle, className);
            return false;
        }

        lock (_gate)
            _lastAllowed = windowHandle;
        return true;
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

    /// <summary>Returns whether a window was asked to go down.</summary>
    private bool Minimize(nint windowHandle, bool onOwnThread)
    {
        if (windowHandle == IntPtr.Zero) return false;

        var root = AppWindowOf(windowHandle);
        var owner = root == windowHandle ? IntPtr.Zero : root;
        var (minimizeOwner, minimizeWindow) = PlanMinimize(
            hasVisibleOwner: owner != IntPtr.Zero && NativeMethods.IsWindowVisible(owner),
            ownerMinimized: owner != IntPtr.Zero && NativeMethods.IsIconic(owner),
            windowShown: NativeMethods.IsWindowVisible(windowHandle) && !NativeMethods.IsIconic(windowHandle));
        var asked = false;
        if (minimizeOwner)
            asked |= MinimizeWindow(owner, onOwnThread);
        if (minimizeWindow)
            asked |= MinimizeWindow(windowHandle, onOwnThread);
        return asked;
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
    /// carries it out with the foreground rights it has. Returns whether it was
    /// asked.
    /// </summary>
    private bool MinimizeWindow(nint windowHandle, bool onOwnThread)
    {
        try
        {
            if (!onOwnThread)
            {
                MinimizeFromAgent(windowHandle);
                return true;
            }
            if (!NativeMethods.ShowWindowAsync(windowHandle, NativeMethods.SW_MINIMIZE))
            {
                _log.LogWarning("ShowWindowAsync(SW_MINIMIZE) returned false for 0x{Hwnd:X}", windowHandle);
                return false;
            }
            _ = WarnIfNotMinimizedAsync(windowHandle);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Minimizing threw for 0x{Hwnd:X}", windowHandle);
            return false;
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
    /// <see cref="AgentShowWindowWait"/>: <see cref="MinimizeFromAgent"/> (#440),
    /// <see cref="Block"/> restoring a minimized fallback (#451), and
    /// <see cref="BringForward"/> restoring an allowed app the student picked on the
    /// overlay (#453). <c>ShowWindow</c> on
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

    public void BringForward(nint windowHandle)
    {
        if (windowHandle == IntPtr.Zero) return;

        // Back on screen before it gets the foreground, as when the restore ran on
        // the caller's thread, the overlay's UI thread. An app that is not
        // responding held that thread up, and every foreground change behind it,
        // until it responded (#453); now it holds it up no longer than a moment,
        // and its window comes back as soon as it responds.
        ShowFromAgent(windowHandle, NativeMethods.SW_RESTORE);
        try
        {
            // Does not wait for the app: Windows queues the activation for its thread.
            if (!NativeMethods.SetForegroundWindow(windowHandle))
                _log.LogDebug("SetForegroundWindow refused 0x{Hwnd:X}", windowHandle);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "SetForegroundWindow threw for 0x{Hwnd:X}", windowHandle);
        }
    }

    private static string CommandName(int command) => command switch
    {
        NativeMethods.SW_MINIMIZE => "SW_MINIMIZE",
        NativeMethods.SW_SHOWNOACTIVATE => "SW_SHOWNOACTIVATE",
        NativeMethods.SW_RESTORE => "SW_RESTORE",
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

    public async Task<bool> HandOverAsync(
        nint offendingWindowHandle,
        nint successorWindowHandle,
        Func<bool>? stillToGoDown = null)
    {
        if (offendingWindowHandle == IntPtr.Zero) return false;
        if (!StillToGoDown(offendingWindowHandle, stillToGoDown)) return false;
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
            if (AsksToMinimizeWithTheKeyboardHandedOver(minimized: NativeMethods.IsIconic(offendingWindowHandle)))
                AskToMinimize(offendingWindowHandle);
            else
                _log.LogDebug("Not asking 0x{Hwnd:X} to minimize: it is down, and the successor has the keyboard", offendingWindowHandle);
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
            // more, so minimizing it next hands the foreground on. The successor is put
            // in place only once it is back on screen.
            _log.LogDebug("Restoring minimized 0x{Hwnd:X}, which has the foreground, to hand it over", offendingWindowHandle);
            // A minimize still on its way to it lands before this restore, so it does
            // not take the window down for this hand-over (#474).
            lock (_gate)
                _minimizeOnItsWay.Remove(offendingWindowHandle);
            try { NativeMethods.ShowWindowAsync(offendingWindowHandle, NativeMethods.SW_RESTORE); }
            catch (Exception ex) { _log.LogError(ex, "ShowWindowAsync(SW_RESTORE) threw for 0x{Hwnd:X}", offendingWindowHandle); }
            await WaitUntilAsync(() => !NativeMethods.IsIconic(offendingWindowHandle), OwnThreadMinimizeTimeout)
                .ConfigureAwait(false);
            if (!StillToGoDown(offendingWindowHandle, stillToGoDown)) return false;
        }
        if (plan != HandOverPlan.AlreadyDown)
        {
            PutAboveEveryAppWindow(successorWindowHandle, offendingWindowHandle);
            AskToMinimize(offendingWindowHandle);
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

    /// <summary>
    /// Puts <paramref name="successorWindowHandle"/> at the top of the windows that
    /// are not topmost: above every app window, <paramref name="goingDown"/>
    /// included. When the window with the foreground minimizes itself, Windows
    /// activates the first other window from the top of the Z-order that is on
    /// screen and not topmost, so the successor must be that window (#475). Right
    /// below the window that goes down, as before, it was below any app window
    /// above that one. A browser restored to be handed over was: placed while the
    /// restore was still going, the overlay ended up below an allowed app, which
    /// got the keyboard. So a later hand-over of the same window, once the restore
    /// is done, puts the successor at the top again.
    /// </summary>
    private void PutAboveEveryAppWindow(nint successorWindowHandle, nint goingDown)
    {
        const uint flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE;
        try
        {
            if (IsTopmost(successorWindowHandle))
            {
                // Leaving the topmost band, a window goes to the top of the others.
                if (!NativeMethods.SetWindowPos(successorWindowHandle, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0, flags))
                    _log.LogWarning("SetWindowPos could not put 0x{Successor:X} above every app window", successorWindowHandle);
                return;
            }
            // Out of the band already: a hand-over still under way took it out. Not
            // back through the band, where a window going down meanwhile passes it
            // over, unless HWND_TOP leaves another app's window above it, as it can.
            NativeMethods.SetWindowPos(successorWindowHandle, NativeMethods.HWND_TOP, 0, 0, 0, 0, flags);
            if (IsFirstAppWindowBut(successorWindowHandle, AppWindowOf(goingDown)))
                return;
            _log.LogDebug("0x{Successor:X} is still below another app's window; taking it through the topmost band", successorWindowHandle);
            if (!NativeMethods.SetWindowPos(successorWindowHandle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, flags) ||
                !NativeMethods.SetWindowPos(successorWindowHandle, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0, flags))
                _log.LogWarning("SetWindowPos could not put 0x{Successor:X} above every app window", successorWindowHandle);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "SetWindowPos threw putting 0x{Successor:X} above every app window", successorWindowHandle);
        }
    }

    private static bool IsTopmost(nint windowHandle) =>
        (NativeMethods.GetWindowLongW(windowHandle, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_TOPMOST) != 0;

    /// <summary>
    /// Whether <paramref name="windowHandle"/> is the first window from the top of
    /// the Z-order that Windows may hand the foreground to (on screen, not topmost,
    /// not a tool window), but for the windows of the app window
    /// <paramref name="appWindow"/>: the one that goes down, with its dialogs.
    /// </summary>
    private static bool IsFirstAppWindowBut(nint windowHandle, nint appWindow)
    {
        for (var window = NativeMethods.GetWindow(windowHandle, NativeMethods.GW_HWNDFIRST);
             window != IntPtr.Zero;
             window = NativeMethods.GetWindow(window, NativeMethods.GW_HWNDNEXT))
        {
            if (window == windowHandle)
                return true;
            if (!NativeMethods.IsWindowVisible(window) || NativeMethods.IsIconic(window))
                continue;
            var exStyle = NativeMethods.GetWindowLongW(window, NativeMethods.GWL_EXSTYLE);
            if ((exStyle & (NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TOOLWINDOW)) == 0 &&
                AppWindowOf(window) != appWindow)
                return false;
        }
        return false;
    }

    /// <summary>
    /// Whether <see cref="HandOverAsync"/> still takes <paramref name="windowHandle"/>
    /// down: not once its caller's <paramref name="stillToGoDown"/> says no (#474).
    /// </summary>
    private bool StillToGoDown(nint windowHandle, Func<bool>? stillToGoDown)
    {
        if (stillToGoDown is null) return true;
        bool still;
        try { still = stillToGoDown(); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Asking whether 0x{Hwnd:X} still has to go down threw; taking it down", windowHandle);
            return true;
        }
        if (!still)
            _log.LogDebug("Not handing 0x{Hwnd:X} over: it no longer has to go down", windowHandle);
        return still;
    }

    /// <summary>
    /// Asks <paramref name="windowHandle"/> to minimize itself for a hand-over, unless
    /// a minimize a hand-over asked of it earlier is still on its way (#474). Its app
    /// carries the request out when it gets to it, after what it is asked meanwhile:
    /// a restore the student sends while the minimize waits, which the minimize then
    /// undoes. The two events of one restore (MINIMIZEEND and FOREGROUND) each ask
    /// for a hand-over, and the overlay gets to them a moment apart: each asked for a
    /// minimize, so an app slow to get to them, as the e2e's Notepad is, had one left
    /// to carry out after the restore the student made once the pause lifted.
    /// </summary>
    private void AskToMinimize(nint windowHandle)
    {
        var tracked = _minimizeStarts.EnsureStarted();
        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (tracked && MinimizeStillOnItsWay(_minimizeOnItsWay.TryGetValue(windowHandle, out var at) ? at : null, now))
            {
                _log.LogDebug(
                    "Not asking 0x{Hwnd:X} to minimize again: the minimize a hand-over asked of it is still on its way",
                    windowHandle);
                return;
            }
            // Before it is asked, so a window that goes down at once is not left
            // counted as on its way.
            if (tracked)
                _minimizeOnItsWay[windowHandle] = now;
        }
        if (!Minimize(windowHandle, onOwnThread: true) && tracked)
        {
            // Nothing was asked to go down (a dialog already out of sight with the
            // window that owns it): nothing is on its way.
            lock (_gate)
            {
                if (_minimizeOnItsWay.TryGetValue(windowHandle, out var at) && at == now)
                    _minimizeOnItsWay.Remove(windowHandle);
            }
        }
    }

    /// <summary>
    /// Whether the minimize a hand-over asked of a window at <paramref name="askedAt"/>
    /// (null when none is waiting) still counts as on its way at <paramref name="now"/>
    /// (both <see cref="Environment.TickCount64"/>), factored out as a pure decision so
    /// it is unit-testable without a live desktop: until the window starts to go
    /// down, or for no longer than <see cref="MinimizeOnItsWayLimit"/> when its app
    /// is not responding.
    /// </summary>
    internal static bool MinimizeStillOnItsWay(long? askedAt, long now) =>
        askedAt is { } at && now - at < (long)MinimizeOnItsWayLimit.TotalMilliseconds;

    /// <summary>
    /// A window started to go down: the minimize a hand-over asked of it, or of a
    /// dialog it owns (#428), is no longer on its way.
    /// </summary>
    private void OnMinimizeStart(nint windowHandle)
    {
        lock (_gate)
        {
            if (_minimizeOnItsWay.Count == 0) return;
            foreach (var asked in _minimizeOnItsWay.Keys.ToArray())
            {
                if (asked == windowHandle || AppWindowOf(asked) == windowHandle)
                    _minimizeOnItsWay.Remove(asked);
            }
        }
    }

    /// <summary>
    /// Whether a hand-over whose successor has the keyboard already asks the window
    /// to minimize, factored out as a pure decision so it is unit-testable without a
    /// live desktop: only while the window is on screen. Minimized already, it has
    /// nothing to take down now, and the request would wait in its app's queue until
    /// the app got to it, after a restore the student sends meanwhile: the window
    /// went down once more. The MINIMIZEEND of a restore that a hand-over made
    /// reaches the agent only after that hand-over asked the window to minimize, so
    /// it asked for a second minimize of a window already down (#474).
    /// </summary>
    internal static bool AsksToMinimizeWithTheKeyboardHandedOver(bool minimized) => !minimized;

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
            _minimizeOnItsWay.Clear();
        }
    }
}
