namespace FocusAgent.Core.Watchdog;

/// <summary>
/// Filesystem and kernel-object names shared between <c>FocusAgent.App</c>
/// and <c>FocusAgent.Watchdog</c>. Centralised here so both processes agree
/// on where to look without taking a project reference on each other.
/// </summary>
public static class AnchorWatchdogPaths
{
    /// <summary>
    /// Kernel mutex the App holds for the lifetime of its process. The
    /// Watchdog probes presence by trying to open this name — open succeeds
    /// while the App is alive, fails with <c>WaitHandleCannotBeOpenedException</c>
    /// when the App is gone. Session-scoped (<c>Local\</c>) so a teacher and
    /// student signed in to the same machine don't observe each other's App.
    /// </summary>
    public const string AppPresenceMutexName = @"Local\Anchor.FocusAgent.AppPresence";

    /// <summary>
    /// Kernel mutex the Watchdog itself takes to enforce single-instance
    /// inside one user session. Belt-and-braces against the StartupTask and
    /// a manual launch racing each other.
    /// </summary>
    public const string WatchdogPresenceMutexName = @"Local\Anchor.FocusAgent.WatchdogPresence";

    /// <summary>
    /// Sentinel file the App writes when the user explicitly quits via the
    /// tray menu or the MainWindow Quit button. Its presence (within the
    /// freshness window) tells the Watchdog the absence is intentional, not
    /// a crash. See <see cref="WatchdogPolicy"/> for the window.
    /// </summary>
    public static string QuitFlagPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Anchor", "FocusAgent", "quit.flag");
}
