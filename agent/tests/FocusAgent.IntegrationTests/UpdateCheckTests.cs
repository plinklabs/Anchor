using System.Diagnostics;
using System.Runtime.Versioning;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// End-to-end proof for #224: the <em>real</em> built agent, run in its dev-only
/// <c>--check-update</c> mode, drives Velopack's actual update-check path
/// (<c>UpdateManager</c> + <c>SimpleFileSource</c>) against a locally-served feed
/// and correctly reports whether a newer release is available.
///
/// <para>
/// The feed is a genuine <c>vpk pack</c> output (RELEASES + the full nupkg +
/// releases.win.json — the same shape the release pipeline uploads to GitHub
/// Releases, #209), served straight from a temp directory. No GitHub, no admin, no
/// actual install: the agent's <c>--check-update</c> mode points a
/// <c>TestVelopackLocator</c> at the temp feed and runs the real check, so this is
/// the closest faithful exercise of the production path short of a real release.
/// </para>
///
/// <para>
/// A pure unit test (which this PR also has, <c>AgentUpdateServiceTests</c>) covers
/// the cadence/gating policy against a fake manager, but it cannot prove the agent
/// actually parses a Velopack feed and selects the newer release — only launching
/// the shipped exe against a real packed feed does that.
/// </para>
///
/// No backend is needed (this is the update-feed path, not a session path), so the
/// spec stays out of the backend-bound <c>AgentE2ECollection</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UpdateCheckTests
{
    [Fact]
    public async Task CheckUpdate_reports_a_newer_release_from_a_local_feed()
    {
        using var feed = await VelopackFeed.PackAsync(version: "9.9.9");

        // The agent believes it is installed at 0.0.1; the feed has 9.9.9.
        var result = await RunCheckUpdateAsync(feed.Directory, currentVersion: "0.0.1");

        Assert.Equal("update-available: 9.9.9", result);
    }

    [Fact]
    public async Task CheckUpdate_reports_up_to_date_when_the_feed_is_not_newer()
    {
        using var feed = await VelopackFeed.PackAsync(version: "9.9.9");

        // The agent is already at the feed's version — no update.
        var result = await RunCheckUpdateAsync(feed.Directory, currentVersion: "9.9.9");

        Assert.Equal("up-to-date", result);
    }

    /// <summary>
    /// Launch the real agent exe in <c>--check-update &lt;feedDir&gt;</c> mode,
    /// pointed at the local feed and told it is currently at
    /// <paramref name="currentVersion"/>, then read back the one-line result the
    /// mode writes (the exe is a WinExe with no console attached, so the result
    /// comes via the result file, like --verify-ds-theme).
    /// </summary>
    private static async Task<string> RunCheckUpdateAsync(string feedDir, string currentVersion)
    {
        if (!File.Exists(TestConfig.AgentExe))
            throw new FileNotFoundException(
                $"Agent exe not found at {TestConfig.AgentExe}. Build it first: " +
                "dotnet build agent/src/FocusAgent.App/FocusAgent.App.csproj -p:Platform=x64 -c Debug",
                TestConfig.AgentExe);

        var resultPath = Path.Combine(Path.GetTempPath(), $"anchor-update-check-{Guid.NewGuid():N}.txt");
        try
        {
            var psi = new ProcessStartInfo(TestConfig.AgentExe) { UseShellExecute = false };
            psi.ArgumentList.Add("--check-update");
            psi.ArgumentList.Add(feedDir);
            psi.Environment["ANCHOR_UPDATE_CURRENT_VERSION"] = currentVersion;
            psi.Environment["ANCHOR_UPDATE_RESULT_PATH"] = resultPath;

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start the agent process.");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException("Agent did not exit within 60s of --check-update.");
            }

            Assert.True(
                File.Exists(resultPath),
                $"Agent --check-update wrote no result file (exit {process.ExitCode}).");
            return (await File.ReadAllTextAsync(resultPath)).Trim();
        }
        finally
        {
            try { File.Delete(resultPath); } catch { }
        }
    }
}
