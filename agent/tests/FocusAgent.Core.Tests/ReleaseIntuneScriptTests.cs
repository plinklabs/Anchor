using System.Text.RegularExpressions;
using FocusAgent.Core.Logging;
using FocusAgent.Core.Startup;

namespace FocusAgent.Core.Tests;

/// <summary>
/// Pins for the #457 Intune install script
/// (<c>agent/scripts/release-assets/Install-AnchorAgent.ps1</c>): that it looks
/// for the agent where the agent and its installer put things, that its help
/// lists every exit code it uses (Intune shows IT only the code), and that every
/// release ships it. Its behavior, run as Intune runs it, is covered end to end
/// by FocusAgent.IntegrationTests/IntuneInstallScriptTests.cs; the release
/// workflow itself only runs on an <c>agent-v*</c> tag, so nothing else checks
/// the wiring before a release.
/// </summary>
public class ReleaseIntuneScriptTests
{
    private static string RepoRoot => FindRepoRoot();

    private static string ScriptPath =>
        Path.Combine(RepoRoot, "agent", "scripts", "release-assets", "Install-AnchorAgent.ps1");

    private static string PackReleasePath =>
        Path.Combine(RepoRoot, "agent", "scripts", "pack-release.ps1");

    private static string ReleaseWorkflowPath =>
        Path.Combine(RepoRoot, ".github", "workflows", "agent-release.yml");

    [Fact]
    public void Script_starts_the_agent_with_the_run_value_its_install_hook_writes()
    {
        var script = ReadScript();

        Assert.Equal(StartupRunKey.ValueName, Assignment(script, "RunValueName"));
        Assert.Equal($@"HKCU:\{StartupRunKey.RunKeyPath}", Assignment(script, "RunKeyPath"));
    }

    [Fact]
    public void Script_logs_next_to_the_agents_own_logs()
    {
        var folder = Assignment(ReadScript(), "AgentLogFolder").Replace('\\', Path.DirectorySeparatorChar);

        Assert.Equal(
            AgentLogPaths.LocalAppDataLogDirectory(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), folder));
    }

    [Fact]
    public void Script_looks_for_the_install_and_setup_that_pack_release_produces()
    {
        // Velopack installs into %LOCALAPPDATA%\<packId>\current\<mainExe> and
        // names the installer <packId>-<channel>-Setup.exe (channel "win").
        var script = ReadScript();
        var pack = File.ReadAllText(PackReleasePath);

        var packId = Regex.Match(pack, @"--packId '(?<v>[^']+)'").Groups["v"].Value;
        var mainExe = Regex.Match(pack, @"\$mainExe = '(?<v>[^']+)'").Groups["v"].Value;
        Assert.NotEmpty(packId);
        Assert.NotEmpty(mainExe);

        Assert.Equal(packId, Assignment(script, "PackId"));
        Assert.Equal(mainExe, Assignment(script, "MainExe"));
        Assert.Equal($"{packId}-win-Setup.exe", Assignment(script, "SetupFileName"));
    }

    [Fact]
    public void Script_help_lists_every_exit_code_the_script_uses()
    {
        // IT sees only the exit code in the Intune console, and the rollout docs
        // (#458) take the table from the help.
        var script = ReadScript();
        var help = script[..script.IndexOf("#>", StringComparison.Ordinal)];
        var code = script[script.IndexOf("#>", StringComparison.Ordinal)..];

        var used = Regex.Matches(code, @"\bexit (?<n>\d+)\b").Select(m => m.Groups["n"].Value).Distinct().ToList();
        Assert.Contains("0", used);
        Assert.Contains("15", used);
        foreach (var n in used)
            Assert.Matches(new Regex($@"(?m)^\s+{n}\s{{2,}}\S"), help);
    }

    [Fact]
    public void PackRelease_puts_the_script_next_to_setup_after_vpk_pack()
    {
        var lines = File.ReadAllLines(PackReleasePath);

        var source = IndexOfCode(lines, l => l.StartsWith("$intuneScriptPath = ", StringComparison.Ordinal));
        var vpkPack = IndexOfCode(lines, l => l.StartsWith("vpk pack", StringComparison.Ordinal));
        var copy = IndexOfCode(lines, l => l.StartsWith("Copy-Item -LiteralPath $intuneScriptPath", StringComparison.Ordinal));

        Assert.True(source >= 0, "pack-release.ps1 never names the Intune install script.");
        Assert.Contains("'release-assets/Install-AnchorAgent.ps1'", lines[source]);
        Assert.True(source < vpkPack, "pack-release.ps1 must check the script exists before the slow `vpk pack`.");
        Assert.True(copy > vpkPack, "pack-release.ps1 must copy the script into the Velopack output after `vpk pack`.");
        Assert.Contains("-Destination $velopackDir", lines[copy]);
        Assert.True(File.Exists(ScriptPath), $"Missing script at {ScriptPath}.");
    }

    [Fact]
    public void ReleaseWorkflow_attaches_the_script_to_the_tags_release_after_vpk_creates_it()
    {
        // Normalise line endings: git checks this out CRLF on Windows.
        var workflow = File.ReadAllText(ReleaseWorkflowPath).Replace("\r\n", "\n");
        var lines = workflow.Split('\n');

        var vpkUpload = Array.FindIndex(lines, l => l.TrimStart().StartsWith("vpk upload github", StringComparison.Ordinal));
        var ghUpload = Array.FindIndex(lines, l => l.TrimStart().StartsWith("gh release upload", StringComparison.Ordinal));
        Assert.True(vpkUpload >= 0, "agent-release.yml no longer runs `vpk upload github`.");
        Assert.True(ghUpload > vpkUpload, "The script must be uploaded after vpk has created the release.");

        Assert.Equal("gh release upload $env:RELEASE_TAG", lines[ghUpload].Trim());
        var step = string.Join('\n', lines[(ghUpload - 8)..(ghUpload + 4)]);
        Assert.Contains("agent/artifacts/velopack/Install-AnchorAgent.ps1", step);
        Assert.Contains("RELEASE_TAG: ${{ github.ref_name }}", step);
        Assert.Contains("GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}", step);
        Assert.Contains("--repo ${{ github.repository }}", step);
    }

    private static string ReadScript()
    {
        Assert.True(File.Exists(ScriptPath), $"Missing script at {ScriptPath}.");
        return File.ReadAllText(ScriptPath).Replace("\r\n", "\n");
    }

    /// <summary>The single-quoted string assigned to <c>$name</c>, as a statement or a param() default.</summary>
    private static string Assignment(string script, string name)
    {
        var match = Regex.Match(script, $@"(?m)^\s*(?:\[string\]\s*)?\${name}\s*=\s*'(?<v>[^']*)'");
        Assert.True(match.Success, $"Install-AnchorAgent.ps1 assigns no string to ${name}.");
        return match.Groups["v"].Value;
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
