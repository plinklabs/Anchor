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
    private const string VelopackAppId = "Anchor.Agent";

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

    /// <summary>
    /// A throwaway Velopack release feed produced by the real <c>vpk pack</c> tool
    /// in a temp directory, removed on dispose. Packs a tiny stand-in exe under the
    /// agent's pack id — the check path only reads the feed/nupkg metadata, so the
    /// payload exe never has to be the real agent.
    /// </summary>
    private sealed class VelopackFeed : IDisposable
    {
        public string Directory { get; }
        private readonly string _root;

        private VelopackFeed(string root, string directory)
        {
            _root = root;
            Directory = directory;
        }

        public static async Task<VelopackFeed> PackAsync(string version)
        {
            var root = Path.Combine(Path.GetTempPath(), $"anchor-vpk-{Guid.NewGuid():N}");
            var srcDir = Path.Combine(root, "src");
            var feedDir = Path.Combine(root, "feed");
            System.IO.Directory.CreateDirectory(srcDir);
            System.IO.Directory.CreateDirectory(feedDir);

            // A minimal but real PE as the package's main exe — vpk requires the
            // --mainExe to exist in the pack dir. The check path never executes it.
            var stubExe = Path.Combine(Environment.SystemDirectory, "where.exe");
            File.Copy(stubExe, Path.Combine(srcDir, "Anchor.Agent.exe"), overwrite: true);

            var vpk = ResolveVpk();
            var psi = new ProcessStartInfo(vpk)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in new[]
                     {
                         "pack",
                         "--packId", VelopackAppId,
                         "--packVersion", version,
                         "--packDir", srcDir,
                         "--mainExe", "Anchor.Agent.exe",
                         "--outputDir", feedDir,
                     })
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start vpk.");
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120)))
                await process.WaitForExitAsync(cts.Token);

            if (process.ExitCode != 0)
            {
                try { System.IO.Directory.Delete(root, recursive: true); } catch { }
                throw new InvalidOperationException(
                    $"vpk pack failed (exit {process.ExitCode}).\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            }

            // Sanity: the SimpleFileSource feed file the agent reads must exist.
            Assert.True(
                File.Exists(Path.Combine(feedDir, "releases.win.json")),
                "vpk pack did not produce releases.win.json in the feed dir.");

            return new VelopackFeed(root, feedDir);
        }

        /// <summary>
        /// Locate the <c>vpk</c> tool. It's a global dotnet tool; resolve it from the
        /// well-known tools dir (works on the CI runner once the workflow installs
        /// it) or fall back to PATH.
        /// </summary>
        private static string ResolveVpk()
        {
            var home = Environment.GetEnvironmentVariable("USERPROFILE")
                       ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var toolPath = Path.Combine(home, ".dotnet", "tools", "vpk.exe");
            if (File.Exists(toolPath)) return toolPath;

            // Fall back to PATH (vpk on the system path). If neither resolves, the
            // pack call below fails loudly with a clear message rather than skipping.
            return "vpk";
        }

        public void Dispose()
        {
            try { System.IO.Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
