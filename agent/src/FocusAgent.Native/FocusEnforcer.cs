using System.Runtime.Versioning;
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

    public void RememberAllowed(nint windowHandle)
    {
        if (windowHandle == IntPtr.Zero) return;
        lock (_gate)
            _lastAllowed = windowHandle;
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

        // Never hand focus back to the app just taken down: the blocked window
        // itself, the app window that owns a blocked dialog, or a dialog of the
        // blocked app (#428).
        if (target == IntPtr.Zero
            || !NativeMethods.IsWindow(target)
            || AppWindowOf(target) == AppWindowOf(offendingWindowHandle))
        {
            _log.LogDebug("Block: no valid prior allowed window to restore");
            return false;
        }

        try
        {
            ForceForeground(target);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ForceForeground threw for 0x{Hwnd:X}", target);
            return false;
        }
    }

    public void Reset()
    {
        lock (_gate)
            _lastAllowed = IntPtr.Zero;
    }

    private static void ForceForeground(nint target)
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
