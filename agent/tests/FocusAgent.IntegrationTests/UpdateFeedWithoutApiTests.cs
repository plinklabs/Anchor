using System.Runtime.Versioning;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// #465: the real agent reads its update feed from the newest release's download
/// address and never calls the GitHub REST API, whose 60 anonymous requests an hour
/// per IP address are shared by every laptop behind a school's address.
///
/// <para>
/// The agent runs in its <c>--check-update &lt;repoUrl&gt;</c> mode, which builds the
/// update manager exactly as production does, over a repository URL that points at
/// <see cref="FakeGithubReleases"/>: GitHub's two redirects from
/// <c>releases/latest/download/&lt;file&gt;</c> to the file, and a 403 rate-limit answer
/// on every API path. The feed is a real <c>vpk pack</c> output. With the agent back
/// on Velopack's <c>GithubSource</c>, which lists the releases through the API, both
/// specs fail: the release on this server is never read.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UpdateFeedWithoutApiTests
{
    private const string LatestDownload = "/plinklabs/Anchor/releases/latest/download/";

    [Fact]
    public async Task Check_and_download_come_from_the_newest_release_without_the_api()
    {
        using var feed = await VelopackFeed.PackAsync(version: "9.9.9");
        using var github = new FakeGithubReleases(feed.Directory, tag: "agent-v9.9.9");

        var result = await UpdateCheckTests.RunCheckUpdateAsync(github.RepoUrl, currentVersion: "0.0.1", download: true);

        var requests = github.RequestPaths;
        var seen = string.Join(", ", requests);
        Assert.True(result == "downloaded: 9.9.9", $"Result '{result}'. Requests: {seen}");
        Assert.Contains(LatestDownload + "releases.win.json", requests);
        Assert.Contains(LatestDownload + feed.FullPackageFileName, requests);
        Assert.False(requests.Any(FakeGithubReleases.IsApiPath), $"The agent called the GitHub API: {seen}");
    }

    [Fact]
    public async Task Check_reads_the_newest_release_and_finds_nothing_newer_without_the_api()
    {
        using var feed = await VelopackFeed.PackAsync(version: "9.9.9");
        using var github = new FakeGithubReleases(feed.Directory, tag: "agent-v9.9.9");

        var result = await UpdateCheckTests.RunCheckUpdateAsync(github.RepoUrl, currentVersion: "9.9.9");

        // A failed check also reads "up-to-date" (the manager swallows feed errors),
        // so the feed index must really have been read from the release.
        var requests = github.RequestPaths;
        var seen = string.Join(", ", requests);
        Assert.Equal("up-to-date", result);
        Assert.Contains(LatestDownload + "releases.win.json", requests);
        Assert.False(requests.Any(FakeGithubReleases.IsApiPath), $"The agent called the GitHub API: {seen}");
    }
}
