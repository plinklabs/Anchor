namespace FocusAgent.Core.Updates;

/// <summary>
/// Where the agent reads its Velopack update feed on GitHub Releases without going
/// through the GitHub REST API (#465).
///
/// <para>
/// Velopack's <c>GithubSource</c> lists the releases through <c>api.github.com</c>,
/// which allows 60 anonymous requests an hour per public IP address. Every laptop
/// on a school network shares one address, so a morning of laptops starting up
/// spends that budget in minutes and the rest of the checks fail. GitHub also
/// serves each asset of the newest release at a fixed address,
/// <c>https://github.com/&lt;owner&gt;/&lt;repo&gt;/releases/latest/download/&lt;file&gt;</c>,
/// which redirects to the download itself and isn't part of the API. That is all a
/// static Velopack feed needs: the feed index (<c>releases.win.json</c>) and the
/// packages it names, all assets of the same release.
/// </para>
///
/// <para>
/// "Latest" is the release GitHub marks as such: the newest published release that
/// isn't a pre-release or a draft. Agent releases are the only full releases the
/// repository publishes (the extension ships to the Edge Add-ons store, the cloud
/// tier on a push to main, and the extension test ring's hosting release,
/// <c>extension-test-ring</c>, is a pre-release, #497), so that is always the
/// newest agent release.
/// </para>
/// </summary>
public static class GithubReleaseFeed
{
    /// <summary>
    /// The base address of the newest release's assets for <paramref name="repoUrl"/>
    /// (<c>https://github.com/&lt;owner&gt;/&lt;repo&gt;</c>), with a trailing slash so a
    /// file name can be appended to it.
    /// </summary>
    public static string LatestDownloadUrl(string repoUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoUrl);

        var url = repoUrl.Trim().TrimEnd('/');
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            url = url[..^".git".Length];

        return url + "/releases/latest/download/";
    }
}
