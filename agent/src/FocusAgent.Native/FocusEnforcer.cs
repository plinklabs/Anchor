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
/// down the moment the app responds.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FocusEnforcer : IFocusEnforcer
{
    /// <summary>
    /// How long a window gets to go down, whether it minimizes itself or the agent
    /// minimizes it, before the agent logs that its app is not responding. An app
    /// that responds does it within about 100 ms.
    /// </summary>
    internal static readonly TimeSpan OwnThreadMinimizeTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long <see cref="Minimize"/> waits for the agent's <c>ShowWindow</c> before
    /// it returns (#440). An app that responds is down well within it, so the caller
    /// finds the window down, as when the call ran on the caller's thread: the
    /// overlay it shows next must come up after the window went down, or the
    /// foreground moves on to another window. An app that is not responding holds
    /// the caller up no longer than this.
    /// </summary>
    internal static readonly TimeSpan AgentMinimizeWait = TimeSpan.FromMilliseconds(250);

    private readonly ILogger<FocusEnforcer> _log;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<nint, long> _pendingMinimizes = new();
    private readonly ConcurrentDictionary<nint, long> _minimizingFromAgent = new();
    private long _minimizeGeneration;
    private nint _lastAllowed;

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
    /// Minimizes one window with the agent's own <c>ShowWindow</c>, on a thread of
    /// its own (#440). Minimized from the agent, the window stays the foreground
    /// window, which the session-start sweep, the paused browser and the overlay
    /// rely on: a window Windows brought forward instead (the desktop, say) would
    /// count as allowed and hide the overlay at once. But <c>ShowWindow</c> on
    /// another thread's window waits until that thread handles messages again. Run
    /// on the caller's thread, an app that was not responding held up the agent's
    /// UI thread, and every foreground change behind it, until the app responded,
    /// and the window went down no sooner. Now the caller waits no longer than
    /// <see cref="AgentMinimizeWait"/>, and the window goes down the moment its app
    /// responds. A second request for a window that is still on its way down adds
    /// nothing, so it is dropped.
    /// </summary>
    private void MinimizeFromAgent(nint windowHandle)
    {
        var request = Interlocked.Increment(ref _minimizeGeneration);
        if (!_minimizingFromAgent.TryAdd(windowHandle, request))
        {
            _log.LogDebug("0x{Hwnd:X} is already being minimized", windowHandle);
            return;
        }
        // A thread of its own rather than the thread pool: a ShowWindow that waits
        // for an app that is not responding ties its thread up for as long as the
        // app hangs.
        var minimizing = Task.Factory.StartNew(() =>
        {
            try
            {
                if (!NativeMethods.ShowWindow(windowHandle, NativeMethods.SW_MINIMIZE))
                    _log.LogWarning("ShowWindow(SW_MINIMIZE) returned false for 0x{Hwnd:X}", windowHandle);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "ShowWindow(SW_MINIMIZE) threw for 0x{Hwnd:X}", windowHandle);
            }
            finally
            {
                _minimizingFromAgent.TryRemove(new KeyValuePair<nint, long>(windowHandle, request));
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _ = WarnIfStillMinimizingAsync(windowHandle, request);
        minimizing.Wait(AgentMinimizeWait);
    }

    /// <summary>
    /// Logs that the app of a window the agent is minimizing is not responding,
    /// when its <c>ShowWindow</c> has not returned <see cref="OwnThreadMinimizeTimeout"/>
    /// later. Not judged by <c>IsIconic</c>: Windows marks the window minimized
    /// before its app has handled the request, while the window is still on screen.
    /// </summary>
    private async Task WarnIfStillMinimizingAsync(nint windowHandle, long request)
    {
        await Task.Delay(OwnThreadMinimizeTimeout).ConfigureAwait(false);
        if (_minimizingFromAgent.TryGetValue(windowHandle, out var current) && current == request)
        {
            _log.LogWarning(
                "0x{Hwnd:X} [{ClassName}] did not minimize within {TimeoutMs} ms: its app is not responding. It goes down as soon as the app responds",
                windowHandle, ClassNameOf(windowHandle), OwnThreadMinimizeTimeout.TotalMilliseconds);
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
            // The caller surfaces the overlay. Minimize from the agent, as the sweep
            // does: the blocked window then stays the foreground window, where a
            // window Windows brought forward instead (the desktop, say) would count
            // as allowed and hide the overlay straight away.
            _log.LogDebug("Block: no valid prior allowed window to restore");
            Minimize(offendingWindowHandle);
            return false;
        }

        if (plan == FallbackPlan.Restore)
        {
            // Back on screen before the blocked window goes down, so Windows can
            // hand it the foreground.
            try { NativeMethods.ShowWindow(target, NativeMethods.SW_SHOWNOACTIVATE); }
            catch (Exception ex) { _log.LogError(ex, "ShowWindow(SW_SHOWNOACTIVATE) threw for 0x{Hwnd:X}", target); }
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

    public void Reset()
    {
        lock (_gate)
            _lastAllowed = IntPtr.Zero;
    }
}
