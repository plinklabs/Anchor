using FocusAgent.Core.Logging;
using FocusAgent.Core.Settings;

namespace FocusAgent.Core.Tests;

public class AgentSettingsTests
{
    [Fact]
    public void BackendSettings_DefaultsHubPathToSessionHub()
    {
        var backend = new BackendSettings();

        Assert.Equal("/hubs/session", backend.HubPath);
    }

    [Fact]
    public void BackendSettings_RoundTripsBaseUrl()
    {
        var backend = new BackendSettings { BaseUrl = "https://anchor.example/" };

        Assert.Equal("https://anchor.example/", backend.BaseUrl);
    }

    [Fact]
    public void UpdateSettings_DefaultsAreSafeForAShippedAgent()
    {
        var update = new UpdateSettings();

        // On by default (a shipped agent keeps itself current), stable line only,
        // pointed at the project repo, with a sane re-check cadence.
        Assert.True(update.Enabled);
        Assert.False(update.AllowPrerelease);
        Assert.Equal("https://github.com/plinklabs/Anchor", update.GithubRepoUrl);
        Assert.Equal(TimeSpan.FromHours(6), update.CheckInterval);
    }

    [Fact]
    public void AgentLogPaths_PointsAtLocalAppDataAnchorFocusAgentLogs()
    {
        var path = AgentLogPaths.LocalAppDataLogDirectory();

        Assert.EndsWith(Path.Combine("Anchor", "FocusAgent", "logs"), path);
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            path);
    }
}
