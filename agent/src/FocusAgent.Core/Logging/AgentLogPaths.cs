namespace FocusAgent.Core.Logging;

public static class AgentLogPaths
{
    /// <summary>
    /// The environment variable that moves a dev or test launch's logs (#446, #463):
    /// the agent e2e points every agent it launches at a temporary folder with it, so
    /// a test agent never writes into the log of an agent installed on the same box.
    /// It is the environment form of the <c>Diagnostics:LogDirectory</c> setting.
    /// </summary>
    public const string LogDirectoryEnvVar = "Diagnostics__LogDirectory";

    public static string LocalAppDataLogDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Anchor", "FocusAgent", "logs");

    /// <summary>
    /// The folder a launch writes its daily logs and its crash log to (#463):
    /// <paramref name="overrideDirectory"/> (the <see cref="LogDirectoryEnvVar"/>
    /// value) for a launch with a dev-only flag (<paramref name="devLaunch"/>) that
    /// names one, otherwise <see cref="LocalAppDataLogDirectory"/>. The real agent
    /// passes no dev flag, so its logs never move.
    /// </summary>
    public static string LaunchLogDirectory(bool devLaunch, string? overrideDirectory) =>
        devLaunch && !string.IsNullOrWhiteSpace(overrideDirectory)
            ? overrideDirectory
            : LocalAppDataLogDirectory();

    /// <summary>
    /// Velopack's own log for the app with pack id <paramref name="appId"/>: its
    /// locator, the in-app update checks and the updater write here (#446).
    /// </summary>
    public static string VelopackLogPath(string appId) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "velopack", $"velopack_{appId}.log");
}
