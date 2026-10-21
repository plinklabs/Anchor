using System.Runtime.Versioning;
using System.Text;
using FocusAgent.Core.Focus;
using FocusAgent.Native.Win32;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusAgent.Native;

/// <summary>
/// Minimizes off-list windows and pulls focus back to the most recent allowed
/// window. Uses the documented <c>AttachThreadInput</c> dance because
/// <c>SetForegroundWindow</c> silently fails when called from a background
/// process that doesn't own the active input queue.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FocusEnforcer : IFocusEnforcer
{
    private readonly ILogger<FocusEnforcer> _log;
    private readonly object _gate = new();
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
    public void Minimize(nint windowHandle)
    {
        if (windowHandle == IntPtr.Zero) return;

        var root = AppWindowOf(windowHandle);
        var owner = root == windowHandle ? IntPtr.Zero : root;
        var (minimizeOwner, minimizeWindow) = PlanMinimize(
            hasVisibleOwner: owner != IntPtr.Zero && NativeMethods.IsWindowVisible(owner),
            ownerMinimized: owner != IntPtr.Zero && NativeMethods.IsIconic(owner),
            windowShown: NativeMethods.IsWindowVisible(windowHandle) && !NativeMethods.IsIconic(windowHandle));
        if (minimizeOwner)
            MinimizeWindow(owner);
        if (minimizeWindow)
            MinimizeWindow(windowHandle);
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

    private void MinimizeWindow(nint windowHandle)
    {
        try
        {
            if (!NativeMethods.ShowWindow(windowHandle, NativeMethods.SW_MINIMIZE))
                _log.LogWarning("ShowWindow(SW_MINIMIZE) returned false for 0x{Hwnd:X}", windowHandle);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ShowWindow threw for 0x{Hwnd:X}", windowHandle);
        }
    }

    public bool Block(nint offendingWindowHandle)
    {
        if (offendingWindowHandle == IntPtr.Zero) return false;

        Minimize(offendingWindowHandle);

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
            _log.LogDebug("Block: no valid prior allowed window to restore");
            return false;
        }

        try
        {
            ForceForeground(target, restore: plan == FallbackPlan.RestoreThenForeground);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ForceForeground threw for 0x{Hwnd:X}", target);
            return false;
        }
    }

    /// <summary>What <see cref="Block"/> does with the remembered allowed window.</summary>
    internal enum FallbackPlan
    {
        /// <summary>Nothing to hand focus back to: the caller surfaces the overlay.</summary>
        None,
        /// <summary>The window is on screen: give it the foreground, nothing else.</summary>
        Foreground,
        /// <summary>The window is minimized: restore it, then give it the foreground.</summary>
        RestoreThenForeground,
    }

    /// <summary>
    /// How <see cref="Block"/> hands focus back to the remembered allowed window,
    /// factored out as a pure decision so it is unit-testable without a live
    /// desktop:
    /// <list type="bullet">
    /// <item>Never to the app just taken down: the blocked window itself, the app
    /// window that owns a blocked dialog, or a dialog of the blocked app (#428).</item>
    /// <item>Never to a window that has gone from the screen since it was
    /// remembered: showing it again would put back a window its app hid. The
    /// taskbar's thumbnail flyout re-shown that way crashed explorer (#437).</item>
    /// <item>A minimized window is restored; a window already on screen only gets
    /// the foreground, so its state is left alone.</item>
    /// </list>
    /// </summary>
    internal static FallbackPlan PlanFallback(bool sameAppAsBlocked, bool visible, bool minimized)
    {
        if (sameAppAsBlocked || !visible) return FallbackPlan.None;
        return minimized ? FallbackPlan.RestoreThenForeground : FallbackPlan.Foreground;
    }

    public void Reset()
    {
        lock (_gate)
            _lastAllowed = IntPtr.Zero;
    }

    private static void ForceForeground(nint target, bool restore)
    {
        var currentThread = NativeMethods.GetCurrentThreadId();
        var foregroundHwnd = NativeMethods.GetForegroundWindow();
        var foregroundThread = foregroundHwnd == IntPtr.Zero
            ? 0u
            : NativeMethods.GetWindowThreadProcessId(foregroundHwnd, out _);

        var attached = false;
        try
        {
            if (foregroundThread != 0 && foregroundThread != currentThread)
                attached = NativeMethods.AttachThreadInput(currentThread, foregroundThread, fAttach: true);
            if (restore)
                NativeMethods.ShowWindow(target, NativeMethods.SW_SHOWNOACTIVATE);
            NativeMethods.SetForegroundWindow(target);
        }
        finally
        {
            if (attached)
                NativeMethods.AttachThreadInput(currentThread, foregroundThread, fAttach: false);
        }
    }
}
