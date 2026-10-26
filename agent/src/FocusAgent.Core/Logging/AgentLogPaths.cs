namespace FocusAgent.Core.Logging;

public static class AgentLogPaths
{
    public static string LocalAppDataLogDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Anchor", "FocusAgent", "logs");

    /// <summary>
    /// Velopack's own log for the app with pack id <paramref name="appId"/>: its
    /// locator, the in-app update checks and the updater write here (#446).
    /// </summary>
    public static string VelopackLogPath(string appId) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "velopack", $"velopack_{appId}.log");
}
