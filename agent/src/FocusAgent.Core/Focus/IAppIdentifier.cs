namespace FocusAgent.Core.Focus;

public interface IAppIdentifier
{
    AppInfo? Identify(nint windowHandle, int processId);

    /// <summary>
    /// Brings a window matching <paramref name="rule"/> to the foreground if one
    /// is already running; otherwise tries to launch the app from the rule's
    /// value (path or process name). Returns true if anything was activated or
    /// launched. Publisher-only rules are not actionable and return false. A
    /// running app that is not responding holds the caller, the overlay's UI
    /// thread, up no longer than a moment (#453): its window comes back as soon as
    /// the app responds (<see cref="IFocusEnforcer.BringForward"/>).
    /// </summary>
    bool LaunchOrActivate(AllowedAppRule rule);
}
