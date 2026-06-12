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

    public void Minimize(nint windowHandle)
    {
        if (windowHandle == IntPtr.Zero) return;

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

        if (target == IntPtr.Zero || target == offendingWindowHandle || !NativeMethods.IsWindow(target))
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
