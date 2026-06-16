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
    public void BackendSettings_EnsureValid_PassesForAnAbsoluteHttpsUrl()
    {
        var backend = new BackendSettings { BaseUrl = "https://anchor-api.example.net" };

        var ex = Record.Exception(() => backend.EnsureValid());

        Assert.Null(ex);
    }

    [Theory]
    [InlineData("")]                         // unset / not configured
    [InlineData("   ")]                      // whitespace-only
    [InlineData("/hubs/session")]            // the relative path that crashed agent-v0.1.0
    [InlineData("anchor-api.example.net")]   // missing scheme
    [InlineData("ftp://anchor.example")]     // wrong scheme
    public void BackendSettings_EnsureValid_ThrowsForABlankOrNonAbsoluteHttpUrl(string baseUrl)
    {
        // #247: a release packaged without its per-deployment config bakes in a
        // blank (or otherwise unusable) BaseUrl. EnsureValid must turn that into a
        // readable failure rather than letting the SignalR hub builder throw a
        // bare UriFormatException deep in DI, before any UI.
        var backend = new BackendSettings { BaseUrl = baseUrl };

        var ex = Assert.Throws<InvalidOperationException>(() => backend.EnsureValid());
        Assert.Contains("Backend:BaseUrl", ex.Message);
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
