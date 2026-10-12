using System.Diagnostics;

namespace FocusAgent.Core.Tests;

/// <summary>
/// Coverage for the #403 publish-output guard: <c>pack-release.ps1</c> must fail
/// the release build when the publish folder has no <c>FocusAgent.App.pri</c>.
/// That file holds the compiled XAML and the strings of every agent window;
/// agent-v0.2.0 and agent-v0.2.1 shipped without it and crashed on every start,
/// and nothing in the pipeline noticed.
///
/// <para>
/// These drive the actual <c>agent/scripts/assert-publish-output.ps1</c> the
/// release script runs against stand-in publish folders, and pin that
/// <c>pack-release.ps1</c> runs it before packaging (the release script itself
/// needs a full self-contained publish, which the e2e
/// <c>PublishedPackageTests</c> does).
/// </para>
/// </summary>
public class ReleasePublishOutputTests
{
    private const string PriName = "FocusAgent.App.pri";

    private static string RepoRoot => FindRepoRoot();

    private static string GuardScriptPath =>
        Path.Combine(RepoRoot, "agent", "scripts", "assert-publish-output.ps1");

    private static string PackReleasePath =>
        Path.Combine(RepoRoot, "agent", "scripts", "pack-release.ps1");

    [Fact]
    public void Guard_Passes_WhenThePublishFolderHasThePri()
    {
        var dir = CreatePublishFolder(withPri: true);
        try
        {
            var (exit, stdout, stderr) = RunGuard(dir);

            Assert.True(exit == 0, $"Guard failed ({exit}).\nstdout: {stdout}\nstderr: {stderr}");
            Assert.Contains(PriName, stdout);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Guard_FailsTheBuild_WhenThePriIsMissing()
    {
        // The agent-v0.2.1 publish: the exe and its dependencies, no PRI.
        var dir = CreatePublishFolder(withPri: false);
        try
        {
            var (exit, _, stderr) = RunGuard(dir);

            Assert.True(exit != 0, "Guard should have failed on a publish folder without the PRI.");
            var message = Unwrap(stderr);
            Assert.Contains(PriName, message);
            Assert.Contains("#403", message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Guard_FailsTheBuild_WhenThePriIsEmpty()
    {
        var dir = CreatePublishFolder(withPri: true);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, PriName), []);

            var (exit, _, stderr) = RunGuard(dir);

            Assert.True(exit != 0, "Guard should have failed on an empty PRI.");
            Assert.Contains(PriName, Unwrap(stderr));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PackRelease_RunsTheGuard_BeforeSkipPackAndVpkPack()
    {
        // The guard only protects a release if pack-release.ps1 calls it on the
        // folder it packs, and before both the -SkipPack exit and `vpk pack`.
        Assert.True(File.Exists(PackReleasePath), $"Missing script at {PackReleasePath}.");
        var lines = File.ReadAllLines(PackReleasePath);

        var guard = IndexOfCode(lines, l => l.Contains("assert-publish-output.ps1", StringComparison.Ordinal));
        var skipPack = IndexOfCode(lines, l => l.StartsWith("if ($SkipPack)", StringComparison.Ordinal));
        var vpkPack = IndexOfCode(lines, l => l.StartsWith("vpk pack", StringComparison.Ordinal));

        Assert.True(guard >= 0, "pack-release.ps1 never runs assert-publish-output.ps1.");
        Assert.Contains("-PublishDir $publishDir", lines[guard]);
        Assert.True(skipPack > guard, "The publish-output check must run before the -SkipPack exit.");
        Assert.True(vpkPack > guard, "The publish-output check must run before `vpk pack`.");
    }

    /// <summary>
    /// A stand-in publish folder: the exe and a DLL (empty files), plus a
    /// non-empty PRI when <paramref name="withPri"/> is set.
    /// </summary>
    private static string CreatePublishFolder(bool withPri)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"anchor-publish-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "FocusAgent.App.exe"), []);
        File.WriteAllBytes(Path.Combine(dir, "FocusAgent.App.dll"), []);
        if (withPri)
        {
            File.WriteAllBytes(Path.Combine(dir, PriName), [0x6D, 0x72, 0x6D, 0x5F]);
        }
        return dir;
    }

    /// <summary>
    /// Index of the first code line (trimmed) that matches <paramref name="match"/>,
    /// or -1. Skips the help block and # comments, which mention the same names
    /// as prose.
    /// </summary>
    private static int IndexOfCode(string[] lines, Func<string, bool> match)
    {
        var inHelp = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("<#", StringComparison.Ordinal)) inHelp = true;
            if (inHelp)
            {
                if (line.EndsWith("#>", StringComparison.Ordinal)) inHelp = false;
                continue;
            }
            if (line.StartsWith('#')) continue;
            if (match(line)) return i;
        }
        return -1;
    }

    /// <summary>
    /// PowerShell wraps a long error message to the console width when it
    /// writes it to a redirected stderr; join the lines so a phrase split by
    /// the wrap still matches.
    /// </summary>
    private static string Unwrap(string text) =>
        string.Join(string.Empty, text.Split(["\r\n", "\n"], StringSplitOptions.None).Select(l => l.Trim()));

    /// <summary>
    /// Runs assert-publish-output.ps1 on <paramref name="publishDir"/> with
    /// Windows PowerShell (powershell.exe), as ReleaseConfigSubstitutionTests
    /// does for substitute-config.ps1.
    /// </summary>
    private static (int ExitCode, string StdOut, string StdErr) RunGuard(string publishDir)
    {
        var command = $"& '{GuardScriptPath}' -PublishDir '{publishDir}'";

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start powershell.exe.");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEnd();
        var stdout = stdoutTask.GetAwaiter().GetResult();
        proc.WaitForExit();
        return (proc.ExitCode, stdout, stderr);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, "agent")) &&
                Directory.Exists(Path.Combine(dir, "backend")))
            {
                return dir;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException(
            "Could not locate the repo root (no ancestor dir contains both 'agent' and 'backend').");
    }
}
