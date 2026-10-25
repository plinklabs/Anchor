using FocusAgent.Core.Settings;
using FocusAgent.Core.Updates;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace FocusAgent.App.Updates;

/// <summary>
/// The production <see cref="IAgentUpdateManager"/> (#224): a thin wrapper over
/// Velopack's <see cref="UpdateManager"/>, pointed by default at the GitHub
/// Releases feed the release pipeline publishes (#209). Core owns the cadence and
/// gating (<see cref="AgentUpdateService"/>); this class just performs the real
/// check and download against Velopack.
///
/// <para>
/// It never applies (#449). Velopack's <c>WaitExitThenApplyUpdates</c> starts
/// Update.exe at once, and Update.exe waits only 60 s for the agent to exit before
/// it kills the agent, applies, and (with restart off) leaves it closed. So the
/// download is the last step here: the full package lands in the install's packages
/// folder, and <c>VelopackApp</c>'s auto-apply in Program.Main applies it the next
/// time the agent starts.
/// </para>
///
/// <para>
/// The constructor takes an already-built <see cref="UpdateManager"/> so the same
/// wrapper serves production (a <see cref="GithubSource"/> over the configured
/// repo) and the integration tests (a <see cref="SimpleFileSource"/> over a locally
/// packed feed, either with a <see cref="TestVelopackLocator"/> standing in for a
/// real install or in an install the test assembled). See <see cref="ForGithub"/>,
/// <see cref="ForLocalFeed"/> and <see cref="ForFeedDirectory"/>.
/// </para>
/// </summary>
public sealed class VelopackUpdateManager : IAgentUpdateManager
{
    private readonly UpdateManager _inner;
    private readonly ILogger<VelopackUpdateManager> _log;

    public VelopackUpdateManager(UpdateManager inner, ILogger<VelopackUpdateManager> log)
    {
        _inner = inner;
        _log = log;
    }

    /// <summary>
    /// Build the production manager: a <see cref="GithubSource"/> over the repo in
    /// <see cref="UpdateSettings.GithubRepoUrl"/>. No access token — the agent reads
    /// public release assets anonymously. The default platform locator decides
    /// whether this process is a real Velopack install (it is only after Setup.exe),
    /// which is what gates <see cref="IsInstalled"/>.
    /// </summary>
    public static VelopackUpdateManager ForGithub(UpdateSettings settings, ILogger<VelopackUpdateManager> log)
    {
        var source = new GithubSource(settings.GithubRepoUrl, accessToken: null, prerelease: settings.AllowPrerelease);
        var options = new UpdateOptions();
        return new VelopackUpdateManager(new UpdateManager(source, options), log);
    }

    /// <summary>
    /// Build a manager that reads a locally-served Velopack feed directory (a
    /// <c>vpk pack</c> output: RELEASES + the full/delta nupkg), believing it is an
    /// install of <paramref name="appId"/> at <paramref name="currentVersion"/>.
    /// This is the seam the #224 integration test drives — the real Velopack check
    /// path against a fake feed, no GitHub, no admin, no actual install.
    /// </summary>
    public static VelopackUpdateManager ForLocalFeed(
        string feedDirectory,
        string appId,
        string currentVersion,
        ILogger<VelopackUpdateManager> log)
    {
        var source = new SimpleFileSource(new DirectoryInfo(feedDirectory));
        var locator = new TestVelopackLocator(
            appId,
            currentVersion,
            packagesDir: Path.Combine(Path.GetTempPath(), "anchor-update-check", Guid.NewGuid().ToString("N")),
            logger: null);
        var options = new UpdateOptions();
        return new VelopackUpdateManager(new UpdateManager(source, options, locator), log);
    }

    /// <summary>
    /// Build a manager that reads a locally-served Velopack feed directory but,
    /// unlike <see cref="ForLocalFeed"/>, keeps Velopack's default locator: it
    /// updates whatever install this process really runs from, exactly like
    /// <see cref="ForGithub"/>, only from a directory instead of GitHub Releases.
    /// Dev-only (#449): <c>--test-update-feed</c> selects it so the integration test
    /// can run the real startup update pass of an agent in an install it assembled.
    /// </summary>
    public static VelopackUpdateManager ForFeedDirectory(string feedDirectory, ILogger<VelopackUpdateManager> log)
    {
        var source = new SimpleFileSource(new DirectoryInfo(feedDirectory));
        return new VelopackUpdateManager(new UpdateManager(source, new UpdateOptions()), log);
    }

    public bool IsInstalled => _inner.IsInstalled;

    public async Task<AgentUpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        // Velopack's CheckForUpdatesAsync returns null when already current and
        // throws when the feed is unreachable; both map to "no update" here so a
        // transient failure never crashes the agent (AgentUpdateService also
        // guards, but keeping this null-safe keeps the contract honest).
        UpdateInfo? info;
        try
        {
            info = await _inner.CheckForUpdatesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Velopack feed check failed.");
            return AgentUpdateCheckResult.None;
        }

        if (info is null)
            return AgentUpdateCheckResult.None;

        var version = info.TargetFullRelease?.Version?.ToString();
        return new AgentUpdateCheckResult(IsUpdateAvailable: true, TargetVersion: version, Payload: info);
    }

    public async Task DownloadUpdateAsync(AgentUpdateCheckResult update, CancellationToken cancellationToken = default)
    {
        if (update.Payload is not UpdateInfo info)
            throw new ArgumentException("Update result was not produced by this manager.", nameof(update));

        // Puts the full package (rebuilt from deltas where it can) in the packages
        // folder. That is all the next start's auto-apply needs; see the class remarks
        // for why nothing here may start Update.exe (#449).
        await _inner.DownloadUpdatesAsync(info, progress: null, cancelToken: cancellationToken).ConfigureAwait(false);
    }

    // The same test VelopackApp.Run's auto-apply makes at startup: the newest full
    // package in the packages folder, if it is newer than the installed version.
    public string? GetPendingRestartVersion() => _inner.UpdatePendingRestart?.Version?.ToString();
}
