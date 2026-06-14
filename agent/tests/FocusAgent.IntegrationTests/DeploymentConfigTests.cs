using System.Text.Json;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// End-to-end proof for #203: <c>Backend:BaseUrl</c> + <c>Auth</c> are
/// substitutable per deployment. A release build runs in the Production
/// environment and loads <c>appsettings.Production.json</c> — whose placeholders
/// the release pipeline fills at pack time — so a fork's published agent targets
/// its <em>own</em> backend without editing the committed dev defaults.
///
/// This spec stands in for the pack-time substitution: it writes a Production
/// config next to the real agent exe whose <c>Backend:BaseUrl</c> points at the
/// e2e backend, launches the agent in the Production environment, and — crucially
/// — does NOT pass the <c>Backend__BaseUrl</c> env override. The only thing that
/// can repoint the agent off its localhost dev default and onto the e2e backend
/// is that per-deployment file, so reaching Connected proves the substitution
/// path actually controls which backend the published agent connects to.
///
/// The complementary negative — that the Production file is INERT for a normal
/// dev run (the agent stays on its Development config) — is what every other spec
/// in this suite already relies on: they launch with no environment (Debug build
/// ⇒ Development) and never load this file.
/// </summary>
[Collection(AgentE2ECollection.Name)]
public sealed class DeploymentConfigTests
{
    private readonly BackendFixture _backend;
    public DeploymentConfigTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task SubstitutedProductionConfig_RepointsTheAgentAtTheForksBackend()
    {
        // Stand in for the release pipeline filling appsettings.Production.json's
        // #{...}# placeholders: write a real per-deployment file next to the exe
        // pointing Backend:BaseUrl at the e2e backend. (Auth is left for WAM, which
        // --inject-token bypasses — this spec asserts the backend-URL substitution,
        // the lever that decides which deployment the agent talks to.)
        using var productionConfig = ProductionConfigFile.Write(new
        {
            Backend = new { BaseUrl = _backend.Url },
        });

        await using var agent = AgentProcess.Launch(
            _backend.Url,
            TestConfig.StudentOid,
            environmentName: "Production",
            // The whole point: do NOT let an env var point the agent at the backend.
            // The substituted Production file is the sole source of Backend:BaseUrl.
            pointBackendViaEnv: false);

        // Connected is only reachable if the agent read Backend:BaseUrl from the
        // substituted Production file — its committed dev default is localhost:5276,
        // which nothing is listening on here.
        await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));
    }
}

/// <summary>
/// Writes a substituted <c>appsettings.Production.json</c> next to the built
/// agent exe and restores the original (the committed template the build copied
/// there) on dispose, so a Production-config spec never leaves the build output
/// dirty for the next run or for a non-Production spec.
/// </summary>
internal sealed class ProductionConfigFile : IDisposable
{
    private readonly string _path;
    private readonly string? _original;

    private ProductionConfigFile(string path, string? original)
    {
        _path = path;
        _original = original;
    }

    public static ProductionConfigFile Write(object config)
    {
        var path = Path.Combine(
            Path.GetDirectoryName(TestConfig.AgentExe)!,
            "appsettings.Production.json");
        var original = File.Exists(path) ? File.ReadAllText(path) : null;

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));

        return new ProductionConfigFile(path, original);
    }

    public void Dispose()
    {
        try
        {
            if (_original is not null)
                File.WriteAllText(_path, _original);
            else
                File.Delete(_path);
        }
        catch
        {
            // best-effort restore; the next build's PreserveNewest copy re-lays the
            // committed template if a transient IO error loses the restore here.
        }
    }
}
