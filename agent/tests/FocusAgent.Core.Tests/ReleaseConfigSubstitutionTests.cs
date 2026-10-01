using System.Diagnostics;
using System.Text.RegularExpressions;
using FocusAgent.Core.Settings;
using Microsoft.Extensions.Configuration;

namespace FocusAgent.Core.Tests;

/// <summary>
/// Coverage for the #209 release-time config substitution: the pack pipeline
/// rewrites the published <c>appsettings.Production.json</c> template, replacing
/// its <c>#{TOKEN}#</c> placeholders (#203) with a fork's real backend/Entra
/// values and its own update feed (#360) before <c>vpk pack</c>.
///
/// <para>
/// These drive the actual <c>agent/scripts/substitute-config.ps1</c> the
/// workflow runs — not a re-implementation — against a temp copy of the
/// committed template, then load the result through the SAME
/// <c>Microsoft.Extensions.Configuration.Json</c> stack the running agent uses.
/// That proves end to end that a tagged release produces a config the agent
/// binds to its own backend, and that the strict "no placeholder may survive"
/// guard actually fails the build (so a missing CI variable can't ship a literal
/// <c>#{...}#</c> as a live backend URL).
/// </para>
/// </summary>
public class ReleaseConfigSubstitutionTests
{
    // The value agent-release.yml would compute for a school's fork:
    // https://github.com/${{ github.repository }}.
    private const string ForkRepoUrl = "https://github.com/yourschool/Anchor";

    private static string RepoRoot => FindRepoRoot();

    private static string ScriptPath =>
        Path.Combine(RepoRoot, "agent", "scripts", "substitute-config.ps1");

    private static string TemplatePath =>
        Path.Combine(RepoRoot, "agent", "src", "FocusAgent.App", "appsettings.Production.json");

    private static string BaseConfigPath =>
        Path.Combine(RepoRoot, "agent", "src", "FocusAgent.App", "appsettings.json");

    private static string ReleaseWorkflowPath =>
        Path.Combine(RepoRoot, ".github", "workflows", "agent-release.yml");

    /// <summary>A complete, valid value set for every token in the template.</summary>
    private static Dictionary<string, string> AllValues() => new()
    {
        ["BACKEND_BASE_URL"] = "https://anchor-api-arcadia.example.net",
        ["AUTH_TENANT_ID"] = "11111111-2222-3333-4444-555555555555",
        ["AUTH_CLIENT_ID"] = "66666666-7777-8888-9999-aaaaaaaaaaaa",
        ["AUTH_SCOPE"] = "api://66666666-7777-8888-9999-aaaaaaaaaaaa/.default",
        ["UPDATE_REPO_URL"] = ForkRepoUrl,
    };

    [Fact]
    public void Substitution_FillsAllPlaceholders_AndAgentBindsTheValues()
    {
        var values = AllValues();

        var temp = CopyTemplateToTemp();
        try
        {
            var (exit, stdout, stderr) = RunScript(temp, values);

            Assert.True(exit == 0, $"Script failed ({exit}).\nstdout: {stdout}\nstderr: {stderr}");

            var rewritten = File.ReadAllText(temp);
            // No real #{NAME}# placeholder may survive. (The template's `//`
            // comment mentions `#{...}#` literally as prose; `...` isn't a valid
            // token name, so matching the token shape avoids that false positive
            // — same shape the script's own leftover guard uses.)
            Assert.DoesNotMatch(@"#\{[A-Za-z0-9_]+\}#", rewritten);

            // Load through the agent's own config stack to prove the substituted
            // file is valid JSON AND the keys land where the agent reads them.
            var config = new ConfigurationBuilder()
                .AddJsonFile(temp, optional: false)
                .Build();

            Assert.Equal(values["BACKEND_BASE_URL"], config["Backend:BaseUrl"]);
            Assert.Equal(values["AUTH_TENANT_ID"], config["Auth:TenantId"]);
            Assert.Equal(values["AUTH_CLIENT_ID"], config["Auth:ClientId"]);
            Assert.Equal(values["AUTH_SCOPE"], config["Auth:Scope"]);
            Assert.Equal(ForkRepoUrl, config["Update:GithubRepoUrl"]);
            // A token-less key in the template must be preserved verbatim.
            Assert.Equal(string.Empty, config["Auth:LoginHint"]);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void Substitution_PointsTheUpdateFeedAtTheReleasingRepo_NotUpstream()
    {
        // #360: the committed appsettings.json defaults Update:GithubRepoUrl to
        // plinklabs/Anchor. Before the fix the template had no Update section, so
        // that default survived into every fork's release and its agents took
        // upstream's next build (with upstream's backend and Entra baked in).
        // Layer the real base file under the real substituted template, exactly
        // as App.BuildHost does in Production, and bind the agent's own settings
        // record: the releasing repo must win.
        var temp = CopyTemplateToTemp();
        try
        {
            var (exit, stdout, stderr) = RunScript(temp, AllValues());
            Assert.True(exit == 0, $"Script failed ({exit}).\nstdout: {stdout}\nstderr: {stderr}");

            var config = new ConfigurationBuilder()
                .AddJsonFile(BaseConfigPath, optional: false)
                .AddJsonFile(temp, optional: false)
                .Build();
            var update = config.GetSection(UpdateSettings.SectionName).Get<UpdateSettings>()!;

            Assert.Equal(ForkRepoUrl, update.GithubRepoUrl);
            // The rest of the Update section still comes from the committed base.
            Assert.True(update.Enabled);
            Assert.Equal(TimeSpan.FromHours(6), update.CheckInterval);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Theory]
    [InlineData("AUTH_SCOPE")]
    [InlineData("UPDATE_REPO_URL")]
    public void Substitution_FailsLoudly_WhenAValueIsMissing(string omitted)
    {
        var temp = CopyTemplateToTemp();
        try
        {
            // Supply all but one placeholder; the script must NOT rewrite the
            // file and must exit non-zero, so a missing CI variable can never
            // ship a literal placeholder as a backend URL or update feed.
            var values = AllValues();
            values.Remove(omitted);

            var (exit, _, stderr) = RunScript(temp, values);

            Assert.True(exit != 0, $"Script should have failed on the missing {omitted} value.");
            Assert.Contains(omitted, stderr);

            // The file must be left untouched (placeholders intact) on failure.
            Assert.Contains($"#{{{omitted}}}#", File.ReadAllText(temp));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Theory]
    [InlineData("BACKEND_BASE_URL", "")]
    [InlineData("BACKEND_BASE_URL", "   ")]
    [InlineData("UPDATE_REPO_URL", "")]
    public void Substitution_FailsLoudly_WhenARequiredValueIsBlank(string token, string blank)
    {
        var temp = CopyTemplateToTemp();
        try
        {
            // #247 hardening: a misnamed CI variable resolves to an EMPTY string,
            // not an unset one. The script used to accept "" as a legitimate value
            // and shipped a dead config (empty Backend:BaseUrl -> UriFormatException
            // -> instant crash). A blank required value must now fail the build,
            // exactly like a missing one, leaving the template untouched.
            var values = AllValues();
            values[token] = blank;

            var (exit, _, stderr) = RunScript(temp, values);

            Assert.True(exit != 0, $"Script should have failed on the blank {token} value.");
            Assert.Contains(token, stderr);
            Assert.Contains($"#{{{token}}}#", File.ReadAllText(temp));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void ReleaseWorkflow_BakesInAndUploadsTo_TheReleasingRepo()
    {
        // #360: the substitution above proves the published config carries
        // whatever UPDATE_REPO_URL is. This pins what the release workflow sets it
        // to: the repository running the release, and the same URL vpk uploads the
        // feed to. A hardcoded repo here would send every fork's agents back to one
        // repo's Releases. agent-release.yml only runs on a tag, so nothing else
        // checks this before a release.
        Assert.True(File.Exists(ReleaseWorkflowPath), $"Missing workflow at {ReleaseWorkflowPath}.");
        // Normalise line endings: git checks this out CRLF on Windows.
        var workflow = File.ReadAllText(ReleaseWorkflowPath).Replace("\r\n", "\n");

        var assignments = Regex.Matches(workflow, @"^\s*UPDATE_REPO_URL:\s*(?<value>.+?)\s*$", RegexOptions.Multiline);
        Assert.NotEmpty(assignments);
        Assert.All(assignments, m =>
            Assert.Equal("https://github.com/${{ github.repository }}", m.Groups["value"].Value));

        // Skip comment lines ([^#\n]* before the flag), which mention --repoUrl.
        var uploads = Regex.Matches(workflow, @"^[^#\n]*--repoUrl\s+(?<value>.+?)\s*$", RegexOptions.Multiline);
        Assert.NotEmpty(uploads);
        Assert.All(uploads, m =>
            Assert.Equal("${{ env.UPDATE_REPO_URL }}", m.Groups["value"].Value));
    }

    private static string CopyTemplateToTemp()
    {
        Assert.True(File.Exists(TemplatePath), $"Missing template at {TemplatePath}.");
        var temp = Path.Combine(Path.GetTempPath(), $"anchor-prodcfg-{Guid.NewGuid():N}.json");
        File.Copy(TemplatePath, temp, overwrite: true);
        return temp;
    }

    /// <summary>
    /// Runs substitute-config.ps1 with the given placeholder values passed as a
    /// PowerShell -Values hashtable (the script's testing seam), so the test
    /// doesn't mutate process-wide environment state and can run in parallel.
    /// Uses Windows PowerShell (powershell.exe), present on the CI Windows
    /// runner and the dev box.
    /// </summary>
    private static (int ExitCode, string StdOut, string StdErr) RunScript(
        string targetPath, IReadOnlyDictionary<string, string> values)
    {
        var pairs = string.Join("; ",
            values.Select(kv => $"'{kv.Key}'='{kv.Value.Replace("'", "''")}'"));
        var command =
            $"& '{ScriptPath}' -Path '{targetPath}' -Values @{{ {pairs} }}";

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
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
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
