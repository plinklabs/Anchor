using FocusAgent.Core.Updates;

namespace FocusAgent.Core.Tests;

/// <summary>
/// #465: the agent reads its update feed from the newest release's download address,
/// never through api.github.com. These pin how that address is derived from the
/// configured repository URL; UpdateFeedWithoutApiTests (integration) proves the
/// real agent reads and downloads through it.
/// </summary>
public class GithubReleaseFeedTests
{
    [Theory]
    [InlineData("https://github.com/plinklabs/Anchor")]
    [InlineData("https://github.com/plinklabs/Anchor/")]
    [InlineData("https://github.com/plinklabs/Anchor.git")]
    [InlineData("  https://github.com/plinklabs/Anchor  ")]
    public void LatestDownloadUrl_points_at_the_newest_release_assets(string repoUrl)
    {
        Assert.Equal(
            "https://github.com/plinklabs/Anchor/releases/latest/download/",
            GithubReleaseFeed.LatestDownloadUrl(repoUrl));
    }

    [Fact]
    public void LatestDownloadUrl_keeps_a_forks_own_repository()
    {
        // #360: a fork's agents update from the fork's Releases, never upstream's.
        Assert.Equal(
            "https://github.com/some-school/Anchor/releases/latest/download/",
            GithubReleaseFeed.LatestDownloadUrl("https://github.com/some-school/Anchor"));
    }

    [Fact]
    public void LatestDownloadUrl_never_goes_through_the_api_host()
    {
        var url = GithubReleaseFeed.LatestDownloadUrl("https://github.com/plinklabs/Anchor");

        Assert.Equal("github.com", new Uri(url).Host);
        Assert.DoesNotContain("/repos/", url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void LatestDownloadUrl_rejects_a_blank_repository(string repoUrl)
    {
        Assert.ThrowsAny<ArgumentException>(() => GithubReleaseFeed.LatestDownloadUrl(repoUrl));
    }
}
