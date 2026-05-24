namespace FocusAgent.Watchdog;

public interface IAppLauncher
{
    /// <summary>
    /// Launch <c>FocusAgent.App.exe</c>. Returns <c>true</c> if the launch
    /// was issued without throwing (the App may still fail to start; we only
    /// report whether <c>Process.Start</c> succeeded). Failure to resolve a
    /// path is logged and returns <c>false</c>.
    /// </summary>
    bool TryLaunchApp();
}
