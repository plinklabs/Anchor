using System.Runtime.Versioning;
using FocusAgent.Core.Focus;
using FocusAgent.Native.Win32;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusAgent.Native;

/// <summary>
/// Enumerates visible top-level application windows via <c>EnumWindows</c>,
/// filtered to the set Windows itself would show in alt-tab, and resolves each
/// to an <see cref="AppInfo"/> through the injected <see cref="IAppIdentifier"/>.
/// Feeds the session-start sweep (#104), which minimizes the off-list windows
/// that were already open when a session began.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowEnumerator : IWindowEnumerator
{
    private readonly IAppIdentifier _identifier;
    private readonly ILogger<WindowEnumerator> _log;

    public WindowEnumerator(IAppIdentifier identifier, ILogger<WindowEnumerator>? log = null)
    {
        _identifier = identifier;
        _log = log ?? NullLogger<WindowEnumerator>.Instance;
    }

    public IReadOnlyList<OpenWindow> GetOpenWindows()
    {
        var windows = new List<OpenWindow>();
        // EnumWindows is synchronous — the callback has run for every top-level
        // window by the time it returns, so the list is fully built here. Never
        // throw out of the callback (Windows handles it badly); swallow per-window
        // probe failures and keep going.
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (TryResolve(hwnd) is { } open)
                    windows.Add(open);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "WindowEnumerator: skipping hwnd 0x{Hwnd:X} after probe threw", hwnd);
            }
            return true; // keep enumerating
        }, IntPtr.Zero);
        return windows;
    }

    private OpenWindow? TryResolve(nint hwnd)
    {
        if (!PassesFilter(hwnd))
            return null;

        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var rawPid);
        var info = _identifier.Identify(hwnd, (int)rawPid);
        return info is null ? null : new OpenWindow(hwnd, info);
    }

    private static bool PassesFilter(nint hwnd) => IsCandidate(
        visible: NativeMethods.IsWindowVisible(hwnd),
        iconic: NativeMethods.IsIconic(hwnd),
        hasOwner: NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero,
        exStyle: NativeMethods.GetWindowLongW(hwnd, NativeMethods.GWL_EXSTYLE),
        titleLength: NativeMethods.GetWindowTextLengthW(hwnd));

    /// <summary>
    /// The alt-tab window test, factored out as a pure predicate so the decision
    /// is unit-testable without a live desktop. A window is a sweepable top-level
    /// app window when it is visible, not already minimized, has no owner (so
    /// dialogs / tool popups owned by a real window aren't swept on their own),
    /// isn't a tool window, and carries a title. Kept deliberately conservative:
    /// the sweep should only ever minimize windows the student can actually see,
    /// never shell or background windows.
    /// </summary>
    internal static bool IsCandidate(bool visible, bool iconic, bool hasOwner, int exStyle, int titleLength)
    {
        if (!visible) return false;
        if (iconic) return false;
        if (hasOwner) return false;
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0) return false;
        if (titleLength <= 0) return false;
        return true;
    }
}
