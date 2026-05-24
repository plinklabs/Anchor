namespace FocusAgent.Watchdog;

/// <summary>
/// Probes whether the supervised FocusAgent.App process is currently alive
/// inside this user session. Implementation is the cross-process mutex name
/// the App holds for its lifetime (<see cref="FocusAgent.Core.Watchdog.AnchorWatchdogPaths.AppPresenceMutexName"/>).
/// </summary>
public interface IAppPresenceProbe
{
    bool IsAppAlive();
}
