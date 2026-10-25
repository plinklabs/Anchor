using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// End-to-end proof for #449: a running agent stays running after its update check
/// downloads a newer release, and the next start hands that release to Velopack's
/// updater.
///
/// <para>
/// The bug: the update pass used to finish with Velopack's
/// <c>WaitExitThenApplyUpdates</c>, which starts Update.exe right away. Update.exe
/// waits only 60 s for the agent to exit, then kills it, applies, and (restart off)
/// leaves the student without the agent until the next login. Now the pass only
/// downloads; <c>VelopackApp</c>'s auto-apply in Program.Main applies the package at
/// the next start.
/// </para>
///
/// <para>
/// Each spec assembles a real Velopack install layout in a temp folder: a copy of
/// the built agent in <c>current\</c> with an <c>sq.version</c> manifest (version
/// <see cref="InstalledVersion"/>), and an <c>Update.exe</c> beside it. Velopack's
/// own locator therefore treats the copied agent as installed, exactly as after
/// Setup.exe. The agent is launched on a normal startup with
/// <c>--test-update-feed</c>, so its real update pass (AgentUpdateService over the
/// production VelopackUpdateManager) reads a feed packed by the real <c>vpk</c>
/// instead of GitHub Releases. Nothing else on the update path is stubbed.
/// </para>
///
/// <para>
/// The install's Update.exe is a recorder (FocusAgent.FakeUpdater) that writes down
/// its arguments and exits. The real updater would replace files, create shortcuts
/// and write an uninstall entry on the machine running the tests, and applying a
/// package is Velopack's job, not the agent's. What the agent controls, and what
/// these specs pin, is when it starts the updater: never while it runs, and at the
/// next start with the downloaded package.
/// </para>
///
/// No backend is needed (the update path never talks to it), so the specs stay out
/// of <c>AgentE2ECollection</c>. They run in their own non-parallel collection: the
/// installed agent takes the single-instance key, which would turn any other spec's
/// agent launch into a no-op.
/// </summary>
[Collection(UpdateApplyCollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class UpdateApplyTests(UpdateFeedFixture fixture) : IClassFixture<UpdateFeedFixture>
{
    // The pack id of the stand-in install and feed. Not the shipped Anchor.Agent, so
    // nothing here shares a name with a real install on the machine.
    internal const string PackId = "Anchor.Agent.UpdateE2E";
    private const string InstalledVersion = "0.0.1";
    internal const string FeedVersion = "9.9.9";

    // Startup plus a local 2 MB download; generous for a loaded CI runner.
    private static readonly TimeSpan UpdatePassTimeout = TimeSpan.FromSeconds(90);

    // How long a spec watches for an updater start that must not come: after the
    // update pass (the agent must also still be up then) and after a second launch.
    // Whatever starts the updater has created its process before it moves on, and
    // the recorder writes within a moment of starting; this is that moment, with
    // room for a loaded runner. (The issue's kill came 60 s after the pass, from an
    // updater the pass had started, so the start itself is what the specs catch.)
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(5);

    // How long the recorder may take to write down a start that must come.
    private static readonly TimeSpan RecordTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Running_agent_downloads_an_update_without_starting_the_updater_and_the_next_start_hands_it_over()
    {
        var feed = fixture.Feed;
        using var install = FakeAgentInstall.Create(PackId, InstalledVersion);

        await using (var agent = LaunchInstalledAgent(install, feed))
        {
            await WaitForDownloadedUpdateAsync(agent, install, feed);

            await Task.Delay(SettleTime);
            Assert.False(agent.HasExited, "The agent exited after downloading the update.");
            Assert.NotNull(await agent.TryGetStatusAsync());
            AssertUpdaterNotStarted(install, "while the agent was running");

            // The student logs off (or quits from the tray); the agent ends cleanly.
            await agent.QuitAsync();
            await agent.WaitForExitAsync(ExitTimeout);
        }

        // The next start, as the login Run entry launches it: no flags at all.
        using var next = StartPlain(install);
        var exitCode = await WaitForExitAsync(next, "the next start (it should hand the update over and exit)");

        Assert.Equal(0, exitCode);
        var invocations = await install.WaitForUpdaterInvocationsAsync(RecordTimeout);
        Assert.True(
            invocations.Count == 1,
            $"Expected the next start to start Update.exe once, got {invocations.Count} time(s). " +
            $"Velopack's log:\n{install.ReadVelopackLog()}");
        var invocation = invocations[0];
        Assert.Contains("apply", invocation);
        Assert.Equal(Path.Combine(install.PackagesDir, feed.FullPackageFileName), ValueAfter(invocation, "--package"));
        Assert.Equal(install.Root, ValueAfter(invocation, "--rootDir"));
        Assert.Equal(next.Id.ToString(), ValueAfter(invocation, "--waitPid"));
        // Velopack starts the agent again on the new version once it has applied.
        Assert.DoesNotContain("--norestart", invocation);
    }

    [Fact]
    public async Task A_second_launch_while_the_agent_runs_leaves_the_downloaded_update_for_a_later_start()
    {
        var feed = fixture.Feed;
        using var install = FakeAgentInstall.Create(PackId, InstalledVersion);

        await using var agent = LaunchInstalledAgent(install, feed);
        await WaitForDownloadedUpdateAsync(agent, install, feed);

        // The student clicks Anchor in the Start menu while it sits in the tray.
        // Applying from there would start Update.exe, which stops every process in
        // the install folder, the running agent included.
        using var second = StartPlain(install);
        var exitCode = await WaitForExitAsync(second, "the second launch (single-instance gating should end it)");

        Assert.Equal(0, exitCode);
        await Task.Delay(SettleTime);
        AssertUpdaterNotStarted(
            install, $"with the agent running (--waitPid tells who: agent {agent.Pid}, second launch {second.Id})");
        Assert.False(agent.HasExited, "The running agent exited after a second launch.");
        var status = await agent.TryGetStatusAsync();
        Assert.Equal(feed.Version, status?.PendingUpdate);
        Assert.True(
            File.Exists(Path.Combine(install.PackagesDir, feed.FullPackageFileName)),
            "The downloaded package is gone after a second launch; the next start has nothing to apply.");
    }

    private static AgentProcess LaunchInstalledAgent(FakeAgentInstall install, VelopackFeed feed) =>
        AgentProcess.Launch(
            // Nothing listens there: the update path needs no backend, and a session
            // never starts.
            backendUrl: $"http://127.0.0.1:{TestConfig.FreeLoopbackPort()}",
            impersonateOid: TestConfig.StudentOid,
            extraArgs: ["--test-update-feed", feed.Directory],
            agentExe: install.AgentExe);

    private static async Task WaitForDownloadedUpdateAsync(AgentProcess agent, FakeAgentInstall install, VelopackFeed feed)
    {
        // The agent reports the version Velopack says the next start applies, once its
        // startup update pass has checked the feed and downloaded the release.
        var status = await agent.WaitForAsync(s => s.PendingUpdate is not null, UpdatePassTimeout);
        Assert.True(
            status?.PendingUpdate == feed.Version,
            $"The agent did not report {feed.Version} as its pending update within " +
            $"{UpdatePassTimeout.TotalSeconds:N0}s (last: {status?.PendingUpdate ?? "<none>"}). " +
            $"Velopack's log:\n{install.ReadVelopackLog()}");
        Assert.True(
            File.Exists(Path.Combine(install.PackagesDir, feed.FullPackageFileName)),
            $"The agent reported {feed.Version} pending, but {feed.FullPackageFileName} is not in {install.PackagesDir}.");
    }

    private static void AssertUpdaterNotStarted(FakeAgentInstall install, string when)
    {
        var invocations = install.ReadUpdaterInvocations();
        Assert.True(
            invocations.Count == 0,
            $"Update.exe was started {when}, which ends a running agent:\n  " +
            string.Join("\n  ", invocations.Select(args => string.Join(' ', args))) +
            $"\nVelopack's log:\n{install.ReadVelopackLog()}");
    }

    private static Process StartPlain(FakeAgentInstall install) =>
        Process.Start(new ProcessStartInfo(install.AgentExe) { UseShellExecute = false })
        ?? throw new InvalidOperationException("Failed to start the installed agent.");

    private static async Task<int> WaitForExitAsync(Process process, string what)
    {
        using var cts = new CancellationTokenSource(ExitTimeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"No exit within {ExitTimeout.TotalSeconds:N0}s from {what}.");
        }
        return process.ExitCode;
    }

    private static string? ValueAfter(IReadOnlyList<string> args, string flag)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (args[i] == flag) return args[i + 1];
        return null;
    }
}

/// <summary>Packs the specs' feed (one <see cref="UpdateApplyTests.FeedVersion"/> release) once.</summary>
public sealed class UpdateFeedFixture : IAsyncLifetime
{
    internal VelopackFeed Feed { get; private set; } = null!;

    public async Task InitializeAsync() =>
        Feed = await VelopackFeed.PackAsync(UpdateApplyTests.FeedVersion, UpdateApplyTests.PackId);

    public Task DisposeAsync()
    {
        Feed?.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>
/// A Velopack install layout around a copy of the built agent, in a temp folder that
/// is removed on dispose:
/// <code>
///   &lt;root&gt;\Update.exe        the recording stand-in (FocusAgent.FakeUpdater)
///   &lt;root&gt;\current\          a copy of the agent's build output + sq.version
///   &lt;root&gt;\packages\         created by Velopack; downloads land here
/// </code>
/// Velopack's Windows locator recognizes an install by exactly this: an Update.exe
/// in the parent folder of the running exe and a valid <c>sq.version</c> beside it.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class FakeAgentInstall : IDisposable
{
    private const string InvocationsFileName = "update-invocations.jsonl";

    public string Root { get; }
    public string CurrentDir => Path.Combine(Root, "current");
    public string PackagesDir => Path.Combine(Root, "packages");
    public string AgentExe => Path.Combine(CurrentDir, Path.GetFileName(TestConfig.AgentExe));
    private string UpdateExe => Path.Combine(Root, "Update.exe");
    private readonly string _packId;

    private FakeAgentInstall(string root, string packId)
    {
        Root = root;
        _packId = packId;
    }

    public static FakeAgentInstall Create(string packId, string version)
    {
        var buildDir = Path.GetDirectoryName(TestConfig.AgentExe)!;
        if (!File.Exists(TestConfig.AgentExe))
            throw new FileNotFoundException(
                $"Agent exe not found at {TestConfig.AgentExe}. Build it first: " +
                "dotnet build agent/src/FocusAgent.App/FocusAgent.App.csproj -p:Platform=x64 -c Debug",
                TestConfig.AgentExe);
        var updaterDir = FakeUpdaterDir();
        if (!File.Exists(Path.Combine(updaterDir, "Update.exe")))
            throw new FileNotFoundException(
                $"The recording Update.exe was not found in {updaterDir}; it is built with the integration tests " +
                "(ProjectReference to FocusAgent.FakeUpdater).", Path.Combine(updaterDir, "Update.exe"));

        var install = new FakeAgentInstall(Path.Combine(Path.GetTempPath(), $"anchor-install-{Guid.NewGuid():N}"), packId);
        try
        {
            CopyDirectory(buildDir, install.CurrentDir);
            File.WriteAllText(Path.Combine(install.CurrentDir, "sq.version"), Manifest(packId, version));
            foreach (var file in Directory.EnumerateFiles(updaterDir, "Update.*"))
                File.Copy(file, Path.Combine(install.Root, Path.GetFileName(file)));

            // Velopack's download also refreshes the install's Update.exe from the
            // downloaded package, which would swap the recorder for a real updater.
            // Read-only, that refresh fails; Velopack logs it and the package itself
            // still lands, which is all the specs look at.
            File.SetAttributes(install.UpdateExe, File.GetAttributes(install.UpdateExe) | FileAttributes.ReadOnly);
        }
        catch
        {
            install.Dispose();
            throw;
        }
        return install;
    }

    /// <summary>
    /// Wait until the install's Update.exe has recorded at least one start (or the
    /// timeout passes), then return every start so far. A start's record lands a
    /// moment after the process that started it has moved on.
    /// </summary>
    public async Task<IReadOnlyList<string[]>> WaitForUpdaterInvocationsAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (ReadUpdaterInvocations().Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        // One more moment, so a second start racing the first is counted too.
        await Task.Delay(500);
        return ReadUpdaterInvocations();
    }

    /// <summary>Every start of the install's Update.exe so far, as its argument list.</summary>
    public IReadOnlyList<string[]> ReadUpdaterInvocations()
    {
        var path = Path.Combine(Root, InvocationsFileName);
        if (!File.Exists(path)) return [];
        return File.ReadAllLines(path)
            .Where(line => line.Length > 0)
            .Select(line => JsonSerializer.Deserialize<string[]>(line) ?? [])
            .ToList();
    }

    /// <summary>
    /// The last lines of Velopack's own log for this pack id, for failure messages:
    /// what each launch's locator found and whether it started the updater.
    /// </summary>
    public string ReadVelopackLog(int lines = 60)
    {
        try
        {
            using var stream = new FileStream(VelopackLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var all = reader.ReadToEnd().Split('\n');
            return string.Join('\n', all.TakeLast(lines));
        }
        catch (Exception ex)
        {
            return $"<unreadable: {ex.Message}>";
        }
    }

    // Velopack's locator logs every launch of an installed app here, by pack id.
    private string VelopackLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "velopack", $"velopack_{_packId}.log");

    // The same nuspec shape vpk writes as sq.version into an install's current\.
    private static string Manifest(string packId, string version) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd">
        <metadata>
        <id>{packId}</id>
        <title>{packId}</title>
        <description>{packId}</description>
        <authors>{packId}</authors>
        <version>{version}</version>
        <channel>win</channel>
        <mainExe>{Path.GetFileName(TestConfig.AgentExe)}</mainExe>
        <os>win</os>
        <rid>win-x64</rid>
        </metadata>
        </package>
        """;

    // FocusAgent.FakeUpdater's output, built in the same configuration as this suite.
    private static string FakeUpdaterDir()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('\\', '/')).Parent!.Name;
        return Path.Combine(
            TestConfig.RepoRoot, "agent", "tests", "FocusAgent.FakeUpdater", "bin", configuration, "net10.0");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(UpdateExe)) File.SetAttributes(UpdateExe, FileAttributes.Normal);
        }
        catch
        {
            // best-effort
        }

        // A just-exited agent can hold its files for a moment.
        for (var attempt = 0; attempt < 10 && Directory.Exists(Root); attempt++)
        {
            try { Directory.Delete(Root, recursive: true); }
            catch { Thread.Sleep(500); }
        }

        try
        {
            File.Delete(VelopackLogPath);
        }
        catch
        {
            // best-effort: another launch may still have it open
        }
    }
}

/// <summary>
/// The installed agent these specs launch takes the agent's single-instance key, so
/// they run serially and never alongside the other agent specs.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UpdateApplyCollection
{
    public const string Name = "update-apply";
}
