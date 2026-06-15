using System.Diagnostics;
using Microsoft.Extensions.Configuration;

namespace FocusAgent.Core.Tests;

/// <summary>
/// Coverage for the #209 release-time config substitution: the pack pipeline
/// rewrites the published <c>appsettings.Production.json</c> template, replacing
/// its <c>#{TOKEN}#</c> placeholders (#203) with a fork's real backend/Entra
/// values before <c>vpk pack</c>.
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
    private static string RepoRoot => FindRepoRoot();

    private static string ScriptPath =>
        Path.Combine(RepoRoot, "agent", "scripts", "substitute-config.ps1");

    private static string TemplatePath =>
        Path.Combine(RepoRoot, "agent", "src", "FocusAgent.App", "appsettings.Production.json");

    [Fact]
    public void Substitution_FillsAllPlaceholders_AndAgentBindsTheValues()
    {
        var backendUrl = "https://anchor-api-arcadia.example.net";
        var tenantId = "11111111-2222-3333-4444-555555555555";
        var clientId = "66666666-7777-8888-9999-aaaaaaaaaaaa";
        var scope = "api://66666666-7777-8888-9999-aaaaaaaaaaaa/.default";

        var temp = CopyTemplateToTemp();
        try
        {
            var (exit, stdout, stderr) = RunScript(
                temp,
                new Dictionary<string, string>
                {
                    ["BACKEND_BASE_URL"] = backendUrl,
                    ["AUTH_TENANT_ID"] = tenantId,
                    ["AUTH_CLIENT_ID"] = clientId,
                    ["AUTH_SCOPE"] = scope,
                });

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

            Assert.Equal(backendUrl, config["Backend:BaseUrl"]);
            Assert.Equal(tenantId, config["Auth:TenantId"]);
            Assert.Equal(clientId, config["Auth:ClientId"]);
            Assert.Equal(scope, config["Auth:Scope"]);
            // A token-less key in the template must be preserved verbatim.
            Assert.Equal(string.Empty, config["Auth:LoginHint"]);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void Substitution_FailsLoudly_WhenAValueIsMissing()
    {
        var temp = CopyTemplateToTemp();
        try
        {
            // Supply all but one placeholder; the script must NOT rewrite the
            // file and must exit non-zero, so a missing CI variable can never
            // ship a literal placeholder as a backend URL.
            var (exit, _, stderr) = RunScript(
                temp,
                new Dictionary<string, string>
                {
                    ["BACKEND_BASE_URL"] = "https://example.net",
                    ["AUTH_TENANT_ID"] = "t",
                    ["AUTH_CLIENT_ID"] = "c",
                    // AUTH_SCOPE intentionally omitted.
                });

            Assert.True(exit != 0, "Script should have failed on the missing AUTH_SCOPE value.");
            Assert.Contains("AUTH_SCOPE", stderr);

            // The file must be left untouched (placeholders intact) on failure.
            Assert.Contains("#{AUTH_SCOPE}#", File.ReadAllText(temp));
        }
        finally
        {
            File.Delete(temp);
        }
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
