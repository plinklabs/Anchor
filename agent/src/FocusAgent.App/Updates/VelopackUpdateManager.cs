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
/// check / download / stage against Velopack.
///
/// <para>
/// The constructor takes an already-built <see cref="UpdateManager"/> so the same
/// wrapper serves both production (a <see cref="GithubSource"/> over the configured
/// repo) and the integration test (a <see cref="SimpleFileSource"/> over a locally
/// packed feed, with a <see cref="TestVelopackLocator"/> standing in for a real
/// install). See <see cref="ForGithub"/> and <see cref="ForLocalFeed"/>.
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

        await _inner.DownloadUpdatesAsync(info, progress: null, cancelToken: cancellationToken).ConfigureAwait(false);
    }

    public void StageUpdateForNextRestart(AgentUpdateCheckResult update)
    {
        if (update.Payload is not UpdateInfo info)
            throw new ArgumentException("Update result was not produced by this manager.", nameof(update));

        // WaitExitThenApplyUpdates with restart:false applies the staged package
        // the next time the (now-exited) agent is started, rather than relaunching
        // now. Students keep working; the new version takes effect on the next
        // ordinary start — no mid-session interruption, no fight with single-
        // instance gating.
        _inner.WaitExitThenApplyUpdates(info.TargetFullRelease, silent: true, restart: false, restartArgs: null);
    }
}
