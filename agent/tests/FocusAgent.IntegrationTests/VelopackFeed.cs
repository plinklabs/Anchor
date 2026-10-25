using System.Diagnostics;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// A throwaway Velopack release feed produced by the real <c>vpk pack</c> tool in a
/// temp directory, removed on dispose (#224, #449). Packs a tiny stand-in exe under
/// the given pack id: the update specs only read the feed and nupkg metadata and
/// move the package around; nothing ever runs the payload exe.
///
/// <para>
/// The feed is the shape the release pipeline uploads to GitHub Releases (#209):
/// releases.win.json + the full nupkg (plus the Setup.exe and portable zip vpk also
/// writes, which no spec uses).
/// </para>
/// </summary>
internal sealed class VelopackFeed : IDisposable
{
    /// <summary>The pack id the release script uses (<c>--packId</c> in pack-release.ps1).</summary>
    public const string AgentPackId = "Anchor.Agent";

    /// <summary>The feed directory: hand this to the agent as its update source.</summary>
    public string Directory { get; }

    /// <summary>The pack id the feed's release was packed under.</summary>
    public string PackId { get; }

    /// <summary>The version of the feed's single release.</summary>
    public string Version { get; }

    /// <summary>The full package's file name, as Velopack names it in a packages folder too.</summary>
    public string FullPackageFileName => $"{PackId}-{Version}-full.nupkg";

    private readonly string _root;

    private VelopackFeed(string root, string directory, string packId, string version)
    {
        _root = root;
        Directory = directory;
        PackId = packId;
        Version = version;
    }

    public static async Task<VelopackFeed> PackAsync(string version, string packId = AgentPackId)
    {
        var root = Path.Combine(Path.GetTempPath(), $"anchor-vpk-{Guid.NewGuid():N}");
        var srcDir = Path.Combine(root, "src");
        var feedDir = Path.Combine(root, "feed");
        System.IO.Directory.CreateDirectory(srcDir);
        System.IO.Directory.CreateDirectory(feedDir);

        // A minimal but real PE as the package's main exe — vpk requires the
        // --mainExe to exist in the pack dir. No spec ever executes it.
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
                     "--packId", packId,
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
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120)))
            await process.WaitForExitAsync(cts.Token);

        if (process.ExitCode != 0)
        {
            try { System.IO.Directory.Delete(root, recursive: true); } catch { }
            throw new InvalidOperationException(
                $"vpk pack failed (exit {process.ExitCode}).\nSTDOUT:\n{await stdout}\nSTDERR:\n{await stderr}");
        }

        var feed = new VelopackFeed(root, feedDir, packId, version);

        // Sanity: the SimpleFileSource feed file and the full package the agent reads must exist.
        Assert.True(
            File.Exists(Path.Combine(feedDir, "releases.win.json")),
            "vpk pack did not produce releases.win.json in the feed dir.");
        Assert.True(
            File.Exists(Path.Combine(feedDir, feed.FullPackageFileName)),
            $"vpk pack did not produce {feed.FullPackageFileName} in the feed dir.");

        return feed;
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
        // pack call fails loudly with a clear message rather than skipping.
        return "vpk";
    }

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
