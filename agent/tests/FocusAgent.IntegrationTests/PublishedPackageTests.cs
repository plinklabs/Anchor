using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// End-to-end proof for #403: the agent <em>as the release ships it</em> starts.
///
/// <para>
/// agent-v0.2.0 and agent-v0.2.1 crashed on every start because the unpackaged
/// <c>dotnet publish</c> in the release script left out <c>FocusAgent.App.pri</c>,
/// which holds the compiled XAML of every window and the .resw strings, so
/// <c>MainWindow.InitializeComponent()</c> threw a XamlParseException. The rest of
/// this suite launches the x64 Debug <em>build</em>, which always had the file, so
/// nothing caught it. This spec runs the real release script
/// (<c>agent/scripts/pack-release.ps1 -SkipPack</c>: the same publish, publish
/// check and config substitution agent-release.yml runs, minus <c>vpk pack</c>)
/// and launches the agent from its publish folder, the folder Velopack packages.
/// </para>
/// </summary>
[Trait("Category", "Published")]
[Collection(PublishedPackageCollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class PublishedPackageTests(PublishedPackageFixture publish)
{
    // The #403 crash took the process down during startup. A window that is
    // still up, in a live process, this long after it appeared got past every
    // InitializeComponent call.
    private static readonly TimeSpan StayUpTime = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task Published_agent_shows_its_main_window()
    {
        await using var agent = AgentSelfTestProcess.Launch(
            AgentSelfTestProcess.ShowTestMainWindowArg, publish.AgentExe);

        var hwnd = await agent.WaitForWindowAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(StayUpTime);

        Assert.False(
            agent.HasExited,
            "The published agent exited after showing its main window; see " +
            $"{Path.Combine(AgentTestLogs.Folder, "startup-error.log")} (the run deletes it; set " +
            "Diagnostics__LogDirectory before the run to keep it) and the Application event log.");
        Assert.True(WindowCapture.IsWindow(hwnd), "The published agent's main window closed on its own.");
    }

    [Fact]
    public async Task Published_agent_loads_its_windows_and_strings_from_the_shipped_pri()
    {
        // --verify-i18n parses a real window (GuidedInstallWindow, compiled XAML
        // from the PRI) and resolves strings through MRT (the .resw, same PRI).
        // Dutch proves the localized catalogue shipped, not just the fallback.
        var strings = await I18nTests.RunVerifyI18nAsync("nl-NL", publish.AgentExe);

        Assert.Equal("Nog één stap", strings["window.guided_headline"]);
        Assert.Equal("STORE OPENEN", strings["window.guided_openstore"]);
        Assert.Equal("Anchor openen", strings["code.tray_open"]);
        Assert.Equal("Verbonden", strings["code.status_connected"]);
    }
}

/// <summary>
/// Runs <c>pack-release.ps1 -SkipPack</c> once for the collection. The script
/// publishes into its fixed <c>agent/artifacts/publish</c> folder (gitignored),
/// clearing it first, and fails if the publish check or the config substitution
/// fails, which fails every spec here with the script's output.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PublishedPackageFixture : IAsyncLifetime
{
    // A Release self-contained publish of the app, the witness host and the
    // design-system binding. About a minute on a warm dev box; leave a cold CI
    // runner plenty of room.
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromMinutes(20);

    // Stand-in per-deployment config: substitute-config.ps1 requires every
    // value, and the self-test modes never contact the backend or sign in.
    private static readonly IReadOnlyDictionary<string, string> DeploymentConfig = new Dictionary<string, string>
    {
        ["BACKEND_BASE_URL"] = "https://anchor-api.example.invalid",
        ["AUTH_TENANT_ID"] = "11111111-2222-3333-4444-555555555555",
        ["AUTH_CLIENT_ID"] = "66666666-7777-8888-9999-aaaaaaaaaaaa",
        ["AUTH_SCOPE"] = "66666666-7777-8888-9999-aaaaaaaaaaaa/.default",
        ["UPDATE_REPO_URL"] = "https://github.com/example/Anchor",
    };

    private static string PackReleaseScript =>
        Path.Combine(TestConfig.RepoRoot, "agent", "scripts", "pack-release.ps1");

    /// <summary>The agent exe in the release publish folder.</summary>
    public string AgentExe { get; } =
        Path.Combine(TestConfig.RepoRoot, "agent", "artifacts", "publish", "FocusAgent.App.exe");

    public async Task InitializeAsync()
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(PackReleaseScript);
        psi.ArgumentList.Add("-SkipPack");
        foreach (var (name, value) in DeploymentConfig)
            psi.Environment[name] = value;

        var output = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        if (!process.Start())
            throw new InvalidOperationException("Failed to start powershell.exe for pack-release.ps1.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(PublishTimeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort teardown */ }
            throw new TimeoutException(
                $"pack-release.ps1 -SkipPack did not finish within {PublishTimeout.TotalMinutes:N0} min.\n{Tail(output)}");
        }
        // Drain the async readers before reading the buffer.
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"pack-release.ps1 -SkipPack failed (exit {process.ExitCode}).\n{Tail(output)}");
        if (!File.Exists(AgentExe))
            throw new FileNotFoundException(
                $"pack-release.ps1 -SkipPack succeeded but published no agent exe.\n{Tail(output)}", AgentExe);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Tail(StringBuilder output, int lines = 60)
    {
        string text;
        lock (output) text = output.ToString();
        var all = text.Split(["\r\n", "\n"], StringSplitOptions.None);
        return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Length - lines)));
    }
}

/// <summary>
/// One release publish shared by the published-package specs, run with no other
/// collection in parallel: the publish is CPU-heavy and would slow the
/// timing-sensitive state specs.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PublishedPackageCollection : ICollectionFixture<PublishedPackageFixture>
{
    public const string Name = "published-package";
}
