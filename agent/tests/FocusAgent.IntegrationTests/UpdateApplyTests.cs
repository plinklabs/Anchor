using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// End-to-end proof for how an installed agent takes an update: #449 (a running
/// agent is never killed by the updater; the next start applies a downloaded
/// release) and #456 (an idle agent restarts itself into it, a session is never
/// interrupted, a failed apply does not loop, and a wake re-checks for an update).
///
/// <para>
/// The #449 bug: the update pass used to finish with Velopack's
/// <c>WaitExitThenApplyUpdates</c>, which starts Update.exe right away. Update.exe
/// waits only 60 s for the agent to exit, then kills it, applies, and (restart off)
/// leaves the student without the agent until the next login. Now the pass only
/// downloads. #456 hands the package to the updater again, but only from an agent
/// with no session going on, as the last thing before it exits on its own, with a
/// restart.
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
/// its arguments, waits like the real updater for the <c>--waitPid</c> process to
/// exit (60 s at most) and writes down whether it exited on its own, then exits.
/// The real updater would replace files, create shortcuts and write an uninstall
/// entry on the machine running the tests, and applying a package is Velopack's job,
/// not the agent's. What the agent controls, and what these specs pin, is when it
/// starts the updater, with what, and that it then exits without being killed.
/// </para>
///
/// No backend is needed (the update path never talks to it), so these specs stay out
/// of <c>AgentE2ECollection</c>; the one that needs a session is
/// <see cref="UpdateRestartSessionTests"/>. They run in their own non-parallel
/// collection: the installed agent takes the single-instance key, which would turn
/// any other spec's agent launch into a no-op.
/// </summary>
[Collection(UpdateApplyCollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class UpdateApplyTests(UpdateFeedFixture fixture) : IClassFixture<UpdateFeedFixture>
{
    // The pack id of the stand-in install and feed. Not the shipped Anchor.Agent, so
    // nothing here shares a name with a real install on the machine.
    internal const string PackId = "Anchor.Agent.UpdateE2E";
    internal const string InstalledVersion = "0.0.1";
    internal const string FeedVersion = "9.9.9";

    // Startup plus a local 2 MB download; generous for a loaded CI runner.
    internal static readonly TimeSpan UpdatePassTimeout = TimeSpan.FromSeconds(90);

    // How long a spec watches for an updater start that must not come: after the
    // update pass (the agent must also still be up then) and after a second launch.
    // Whatever starts the updater has created its process before it moves on, and
    // the recorder writes within a moment of starting; this is that moment, with
    // room for a loaded runner. (The issue's kill came 60 s after the pass, from an
    // updater the pass had started, so the start itself is what the specs catch.)
    internal static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(5);

    // How long the recorder may take to write down a start that must come.
    internal static readonly TimeSpan RecordTimeout = TimeSpan.FromSeconds(30);

    internal static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(60);

    // #456: the agent hands over only as the last thing before it exits, and ends
    // itself at the latest 20 s later (UpdateRestarter.ExitDeadline). Update.exe
    // waits 60 s before it kills; the agent must be well inside that.
    internal static readonly TimeSpan HandOverExitBudget = TimeSpan.FromSeconds(20);

    // The agent's config switch for the #456 idle restart (Update:RestartWhenIdle),
    // set per launch through the environment, which the agent reads under
    // --inject-token.
    private static readonly IReadOnlyDictionary<string, string> IdleRestartOff =
        new Dictionary<string, string> { ["Update__RestartWhenIdle"] = "false" };

    [Fact]
    public async Task With_the_idle_restart_off_a_running_agent_downloads_an_update_without_starting_the_updater_and_the_next_start_hands_it_over()
    {
        var feed = fixture.Feed;
        using var install = FakeAgentInstall.Create(PackId, InstalledVersion);

        await using (var agent = LaunchInstalledAgent(install, feed.Directory, IdleRestartOff))
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

        // The idle restart off keeps the agent running with the update downloaded,
        // which is the state this spec needs (an agent in a session is the other way
        // there, see UpdateRestartSessionTests).
        await using var agent = LaunchInstalledAgent(install, feed.Directory, IdleRestartOff);
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

    [Fact]
    public async Task An_idle_agent_restarts_into_a_downloaded_update_and_exits_on_its_own()
    {
        // #456: no session, so once the startup pass has downloaded the release the
        // agent hands it to the updater, with a restart, and exits by itself.
        var feed = fixture.Feed;
        using var install = FakeAgentInstall.Create(PackId, InstalledVersion);

        await using var agent = LaunchInstalledAgent(install, feed.Directory);
        await WaitForAgentExitAsync(agent, install, UpdatePassTimeout + ExitTimeout, "after downloading the update");

        Assert.Equal(0, agent.ExitCode);
        Assert.True(
            File.Exists(Path.Combine(install.PackagesDir, feed.FullPackageFileName)),
            $"The agent restarted, but {feed.FullPackageFileName} is not in {install.PackagesDir} for the updater.");
        AssertRestartedIntoUpdate(install, agent, feed, await install.WaitForUpdaterInvocationsAsync(RecordTimeout));
        await AssertExitedOnItsOwnAsync(install, agent);
    }

    [Fact]
    public async Task After_a_failed_apply_the_restarted_agent_stays_on_its_version_instead_of_restarting_into_it_again()
    {
        // #456, "no loops": what Velopack's updater leaves when its apply fails. The
        // package is still in the packages folder, newer than what runs, and the
        // updater starts the old version again with VELOPACK_RESTART set. Velopack's
        // own startup apply skips such a launch; the agent's idle restart must too,
        // or the two would restart into the same failure over and over.
        var feed = fixture.Feed;
        using var install = FakeAgentInstall.Create(PackId, InstalledVersion);
        install.PlacePackage(feed);

        await using var agent = LaunchInstalledAgent(
            install, feed.Directory, new Dictionary<string, string> { ["VELOPACK_RESTART"] = "true" });
        await WaitForDownloadedUpdateAsync(agent, install, feed);

        await Task.Delay(SettleTime);
        Assert.False(agent.HasExited, "The agent restarted into the update whose apply had just failed.");
        AssertUpdaterNotStarted(install, "right after the updater reported a failed apply (a restart loop)");
        Assert.Equal(feed.Version, (await agent.TryGetStatusAsync())?.PendingUpdate);
    }

    [Fact]
    public async Task On_wake_and_unlock_an_agent_checks_for_an_update_once_the_last_check_is_older_than_the_interval_and_restarts_into_it()
    {
        // #456: the interval timer counts awake time only, so after a night with the
        // lid closed it is nowhere near due. The agent checks on wake and on unlock
        // instead, once the clock says the last check is Update:CheckInterval (6 h)
        // old. The spec moves the agent's update clock on by the time asleep while
        // its timers stay put, as real sleep does (dev POST /simulate-sleep), and
        // sends the wake the way Windows does: the power or session message, posted
        // to the agent's SystemEvents window, so the agent's real wake handling runs.
        using var install = FakeAgentInstall.Create(PackId, InstalledVersion);
        var feedDir = install.CreateFeedDirectory();

        await using var agent = LaunchInstalledAgent(install, feedDir);

        // The startup check finds nothing: the release isn't out yet.
        var startup = await agent.WaitForAsync(s => s.LastUpdateCheck is not null, UpdatePassTimeout);
        Assert.True(startup?.LastUpdateCheck is not null, "The agent's startup update check did not run.");
        await Task.Delay(SettleTime);

        // Unlocked an hour later: not due, so no check.
        await agent.SimulateSleepAsync(TimeSpan.FromHours(1));
        await SendWakeAsync(agent, WindowCapture.WmWtsSessionChange, WindowCapture.WtsSessionUnlock);
        await Task.Delay(SettleTime);
        Assert.Equal(startup!.LastUpdateCheck, (await agent.TryGetStatusAsync())?.LastUpdateCheck);

        // Unlocked again after a night asleep: due, so the agent checks (still nothing).
        await agent.SimulateSleepAsync(TimeSpan.FromHours(14));
        await SendWakeAsync(agent, WindowCapture.WmWtsSessionChange, WindowCapture.WtsSessionUnlock);
        var afterNight = await agent.WaitForAsync(
            s => s.LastUpdateCheck > startup.LastUpdateCheck, TimeSpan.FromSeconds(30));
        Assert.True(
            afterNight?.LastUpdateCheck > startup.LastUpdateCheck,
            "The agent did not check for an update when unlocked 15 h after its last check.");
        await Task.Delay(SettleTime);
        Assert.Null((await agent.TryGetStatusAsync())?.PendingUpdate);
        AssertUpdaterNotStarted(install, "with no update out");

        // The release comes out. The next night, the laptop resumes: due again, so
        // the agent checks, downloads the release and, with no session going on,
        // restarts into it.
        fixture.Feed.PublishTo(feedDir);
        await agent.SimulateSleepAsync(TimeSpan.FromHours(14));
        await SendWakeAsync(agent, WindowCapture.WmPowerBroadcast, WindowCapture.PbtApmResumeSuspend);
        await WaitForAgentExitAsync(agent, install, UpdatePassTimeout + ExitTimeout, "after resuming from a night asleep");

        AssertRestartedIntoUpdate(install, agent, fixture.Feed, await install.WaitForUpdaterInvocationsAsync(RecordTimeout));
        await AssertExitedOnItsOwnAsync(install, agent);
    }

    // Windows' resume or unlock notice, to the agent's SystemEvents window.
    private static async Task SendWakeAsync(AgentProcess agent, uint message, int wParam) =>
        Assert.True(
            await WindowCapture.PostToSystemEventsWindowAsync(agent.Pid, message, wParam, TimeSpan.FromSeconds(30)),
            $"The agent has no SystemEvents window to receive the wake; it isn't watching for wake and unlock. " +
            $"Its windows:\n{WindowCapture.DescribeWindows(agent.Pid)}");

    internal static AgentProcess LaunchInstalledAgent(
        FakeAgentInstall install,
        string feedDirectory,
        IReadOnlyDictionary<string, string>? extraEnvironment = null) =>
        AgentProcess.Launch(
            // Nothing listens there: the update path needs no backend, and a session
            // never starts.
            backendUrl: $"http://127.0.0.1:{TestConfig.FreeLoopbackPort()}",
            impersonateOid: TestConfig.StudentOid,
            extraArgs: ["--test-update-feed", feedDirectory],
            extraEnvironment: extraEnvironment,
            agentExe: install.AgentExe);

    internal static async Task WaitForDownloadedUpdateAsync(AgentProcess agent, FakeAgentInstall install, VelopackFeed feed)
    {
        // The agent reports the version Velopack says the next start applies, once its
        // update pass has checked the feed and downloaded the release.
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

    internal static async Task WaitForAgentExitAsync(AgentProcess agent, FakeAgentInstall install, TimeSpan timeout, string when)
    {
        try
        {
            await agent.WaitForExitAsync(timeout);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"The agent did not restart into the update {when}: still running {timeout.TotalSeconds:N0}s on " +
                $"(pending: {(await agent.TryGetStatusAsync())?.PendingUpdate ?? "<none>"}). " +
                $"Updater starts: {install.ReadUpdaterInvocations().Count}. Velopack's log:\n{install.ReadVelopackLog()}");
        }
    }

    /// <summary>
    /// The agent handed the downloaded package to the updater once, the way #456 asks:
    /// apply it, silently, once this agent has exited, then start the agent again with
    /// the arguments it was launched with (none at sign-in, so it comes back in the
    /// tray with no window).
    /// </summary>
    internal static void AssertRestartedIntoUpdate(
        FakeAgentInstall install, AgentProcess agent, VelopackFeed feed, IReadOnlyList<string[]> invocations)
    {
        Assert.True(
            invocations.Count == 1,
            $"Expected the agent to start Update.exe once, got {invocations.Count} time(s). " +
            $"Velopack's log:\n{install.ReadVelopackLog()}");
        var invocation = invocations[0];
        Assert.Contains("apply", invocation);
        Assert.Contains("--silent", invocation);
        Assert.Equal(Path.Combine(install.PackagesDir, feed.FullPackageFileName), ValueAfter(invocation, "--package"));
        Assert.Equal(install.Root, ValueAfter(invocation, "--rootDir"));
        Assert.Equal(agent.Pid.ToString(), ValueAfter(invocation, "--waitPid"));
        Assert.DoesNotContain("--norestart", invocation);
        Assert.Equal(agent.Arguments, ArgsAfterSeparator(invocation));
    }

    /// <summary>
    /// The updater never had to kill the agent: it exited on its own, well inside the
    /// 60 s the updater waits (#456 acceptance: no "Killing process" for these restarts).
    /// </summary>
    internal static async Task AssertExitedOnItsOwnAsync(FakeAgentInstall install, AgentProcess agent)
    {
        var wait = await install.WaitForUpdaterWaitAsync(RecordTimeout);
        Assert.True(wait is not null, "The updater did not record its wait for the agent.");
        Assert.Equal(agent.Pid, wait!.WaitPid);
        Assert.True(
            wait.ExitedOnItsOwn && wait.WaitedMs <= HandOverExitBudget.TotalMilliseconds,
            $"The agent took {wait.WaitedMs} ms to exit after starting the updater (exited on its own: " +
            $"{wait.ExitedOnItsOwn}); the updater kills it at 60 s and the agent's own deadline is " +
            $"{HandOverExitBudget.TotalSeconds:N0} s.");
    }

    internal static void AssertUpdaterNotStarted(FakeAgentInstall install, string when)
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

    internal static string? ValueAfter(IReadOnlyList<string> args, string flag)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (args[i] == flag) return args[i + 1];
        return null;
    }

    // The arguments Update.exe passes on to the agent it starts: everything after "--".
    private static IReadOnlyList<string> ArgsAfterSeparator(IReadOnlyList<string> args)
    {
        var separator = args.ToList().IndexOf("--");
        return separator < 0 ? [] : args.Skip(separator + 1).ToList();
    }
}

/// <summary>
/// #456 with a session: an installed agent in a session stays on its version while a
/// downloaded update waits, and restarts into it once the teacher ends the session.
/// Needs the e2e backend for the session, so it runs in <see cref="AgentE2ECollection"/>
/// (serially with the other agent specs, which also take the single-instance key)
/// rather than with <see cref="UpdateApplyTests"/>. Same install layout and recording
/// Update.exe; see <see cref="UpdateApplyTests"/>.
/// </summary>
[Collection(AgentE2ECollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class UpdateRestartSessionTests(BackendFixture backend, UpdateFeedFixture fixture)
    : IClassFixture<UpdateFeedFixture>
{
    [Fact]
    public async Task An_agent_in_a_session_stays_on_its_version_until_the_session_ends_then_restarts_into_the_update()
    {
        using var install = FakeAgentInstall.Create(UpdateApplyTests.PackId, UpdateApplyTests.InstalledVersion);
        var feedDir = install.CreateFeedDirectory();
        var api = new BackendClient(backend.Url);

        await using var agent = AgentProcess.Launch(
            backend.Url,
            TestConfig.StudentOid,
            autoJoin: true,
            extraArgs: ["--test-update-feed", feedDir],
            agentExe: install.AgentExe);
        await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(30));

        var classId = await api.FindClassIdAsync();
        var session = await api.StartSessionAsync(classId);
        var joined = await agent.WaitForAsync(s => s.JoinedSessionId == session.Id, TimeSpan.FromSeconds(15));
        Assert.True(
            joined?.JoinedSessionId == session.Id,
            $"The agent did not join the session (joinedSessionId: {joined?.JoinedSessionId?.ToString() ?? "<none>"}).");

        try
        {
            // A release comes out mid-lesson, and the agent's next check downloads it.
            fixture.Feed.PublishTo(feedDir);
            await agent.CheckUpdateAsync();
            await UpdateApplyTests.WaitForDownloadedUpdateAsync(agent, install, fixture.Feed);

            await Task.Delay(UpdateApplyTests.SettleTime);
            Assert.False(agent.HasExited, "The agent restarted into the update during a session.");
            UpdateApplyTests.AssertUpdaterNotStarted(install, "during a session");
            Assert.Equal(session.Id, (await agent.TryGetStatusAsync())?.JoinedSessionId);
        }
        finally
        {
            // The teacher ends the lesson (also on failure, so the next spec's
            // student isn't still in it).
            await api.EndSessionAsync(session.Id);
        }

        await UpdateApplyTests.WaitForAgentExitAsync(
            agent, install, UpdateApplyTests.ExitTimeout, "after the session ended");
        Assert.Equal(0, agent.ExitCode);
        UpdateApplyTests.AssertRestartedIntoUpdate(
            install, agent, fixture.Feed, await install.WaitForUpdaterInvocationsAsync(UpdateApplyTests.RecordTimeout));
        await UpdateApplyTests.AssertExitedOnItsOwnAsync(install, agent);
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
///   &lt;root&gt;\feed\             a feed a spec publishes a release into later (#456)
/// </code>
/// Velopack's Windows locator recognizes an install by exactly this: an Update.exe
/// in the parent folder of the running exe and a valid <c>sq.version</c> beside it.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class FakeAgentInstall : IDisposable
{
    private const string InvocationsFileName = "update-invocations.jsonl";
    private const string WaitsFileName = "update-waits.jsonl";

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

    /// <summary>
    /// An empty feed directory, for a spec whose release comes out while the agent
    /// runs (#456): hand it to the agent, then <see cref="VelopackFeed.PublishTo"/> it.
    /// </summary>
    public string CreateFeedDirectory()
    {
        var feed = Path.Combine(Root, "feed");
        Directory.CreateDirectory(feed);
        return feed;
    }

    /// <summary>
    /// Put the feed's full package in the packages folder, as a download does, before
    /// the agent starts: what a failed apply leaves behind (#456).
    /// </summary>
    public void PlacePackage(VelopackFeed feed)
    {
        Directory.CreateDirectory(PackagesDir);
        File.Copy(
            Path.Combine(feed.Directory, feed.FullPackageFileName),
            Path.Combine(PackagesDir, feed.FullPackageFileName));
    }

    /// <summary>
    /// Wait until the install's Update.exe has recorded how its wait for the
    /// <c>--waitPid</c> process ended (or the timeout passes), and return the first
    /// such record, or null.
    /// </summary>
    public async Task<UpdaterWait?> WaitForUpdaterWaitAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (ReadUpdaterWaits().Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        return ReadUpdaterWaits().FirstOrDefault();
    }

    private IReadOnlyList<UpdaterWait> ReadUpdaterWaits()
    {
        var path = Path.Combine(Root, WaitsFileName);
        if (!File.Exists(path)) return [];
        return File.ReadAllLines(path)
            .Where(line => line.Length > 0)
            .Select(line => JsonSerializer.Deserialize<UpdaterWait>(line, WaitJson)!)
            .ToList();
    }

    private static readonly JsonSerializerOptions WaitJson = new(JsonSerializerDefaults.Web);

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
/// How the recording Update.exe's wait for the agent ended (#456): whether the process
/// it was told to wait for exited on its own within the real updater's 60 s, and after
/// how long. The real updater kills it when it doesn't.
/// </summary>
internal sealed record UpdaterWait(int WaitPid, bool ExitedOnItsOwn, long WaitedMs);

/// <summary>
/// The installed agent these specs launch takes the agent's single-instance key, so
/// they run serially and never alongside the other agent specs.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UpdateApplyCollection
{
    public const string Name = "update-apply";
}
