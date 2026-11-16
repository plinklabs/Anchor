using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using FocusAgent.Core.Logging;
using FocusAgent.Core.Startup;
using FocusAgent.Core.Updates;
using Microsoft.Win32;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// #487: the agent comes back after an update or install that Velopack applies
/// without starting it again: an update an agent before 0.4.0 started
/// (<c>--norestart</c>: Update.exe kills the agent, applies, leaves it closed), and a
/// silent Setup.exe over a running install. The new version's install or update hook
/// hands the agent's start to a waiter outside the install folder, which starts it once
/// the updater or installer has exited. Where the updater restarts the agent itself (the
/// #456 idle restart), the hook leaves it at that.
///
/// <para>
/// Same stand-in install as <see cref="UpdateApplyTests"/>, and the real agent's real
/// hook, run the way Velopack 1.2.0 runs it: the install's Update.exe (or a copy of it
/// playing Setup.exe) stops every process running from the install folder, runs
/// <c>current\FocusAgent.App.exe --veloapp-updated|--veloapp-install &lt;version&gt;</c>,
/// stops the install folder again, and restarts the agent only when Velopack's would.
/// An agent the hook started from the install folder would not survive that second
/// stop; only one started from outside it, after the updater is gone, does. The file
/// swap is left out: it is Velopack's, and it would change nothing the hook sees.
/// </para>
///
/// <para>
/// In production the relaunch starts the agent the way Windows does at sign-in, with
/// no arguments but the log marker. Here the updater or Setup.exe is given the agent's
/// arguments after <c>--</c>, which the relaunch passes on as Velopack's own restart
/// would, so the agent that comes back is one the spec can watch (status endpoint, no
/// WAM, an empty update feed). The hook writes the Run entry to a throwaway key, never
/// the developer's own.
/// </para>
/// </summary>
[Collection(UpdateApplyCollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class UpdateRelaunchTests(UpdateFeedFixture fixture) : IClassFixture<UpdateFeedFixture>
{
    // The acceptance: the agent is back "within about a minute".
    private static readonly TimeSpan RelaunchTimeout = TimeSpan.FromSeconds(60);

    // Long enough for a waiter that should not have started anything to have done so:
    // it starts the agent within a few seconds of the updater's exit.
    private static readonly TimeSpan NoRelaunchSettle = TimeSpan.FromSeconds(15);

    // Velopack kills the update hook at 15 s and the install hook at 30 s; the hook
    // must finish well inside that.
    private static readonly TimeSpan UpdateHookBudget = TimeSpan.FromSeconds(7.5);
    private static readonly TimeSpan InstallHookBudget = TimeSpan.FromSeconds(15);

    private const string RelaunchLogLine = "Started again after an update or install that was applied without restarting the agent";

    [Fact]
    public async Task An_update_applied_without_a_restart_as_an_older_agent_starts_it_brings_the_agent_back()
    {
        using var install = FakeAgentInstall.Create(UpdateApplyTests.PackId, UpdateApplyTests.InstalledVersion);
        install.RunHooksOnApply();
        using var runKey = ThrowawayRunKey.Create();
        var feedDir = install.CreateFeedDirectory();

        await using var agent = UpdateApplyTests.LaunchInstalledAgent(install, feedDir, Environment(install, runKey));
        await WaitUntilUpAsync(agent);

        // What an agent before 0.4.0 starts at the end of its update pass, through
        // Velopack's WaitExitThenApplyUpdates(silent: true, restart: false). The real
        // updater waits 60 s for the agent before it kills it; this one waits 3 s.
        var relaunchPort = TestConfig.FreeLoopbackPort();
        using var updater = Start(
            install.UpdateExe,
            [
                "--silent", "apply",
                "--package", Path.Combine(install.PackagesDir, $"{UpdateApplyTests.PackId}-{UpdateApplyTests.FeedVersion}-full.nupkg"),
                "--waitPid", agent.Pid.ToString(),
                "--norestart",
                "--rootDir", install.Root,
                "--packageDir", install.PackagesDir,
                "--", .. AgentArguments(relaunchPort, feedDir),
            ],
            Environment(install, runKey, ("ANCHOR_FAKE_UPDATER_WAIT_SECONDS", "3")));
        await WaitForExitAsync(updater, "the updater");

        await agent.WaitForExitAsync(TimeSpan.FromSeconds(10));
        AssertHookRan(install, "--veloapp-updated", UpdateHookBudget);

        await using var relaunched = await WaitForRelaunchedAgentAsync(install, relaunchPort);
        await AssertStaysUpAsync(install, relaunched);
        await AssertLoggedRelaunchAsync(install, relaunched);
        Assert.Contains($"Update (pid {updater.Id}) won't start the agent again", install.ReadVelopackLog(200));
        // The hook still points the Run entry at the new version, as before.
        Assert.Equal(StartupRunKey.CommandFor(install.AgentExe), runKey.ReadCommand());
    }

    [Fact]
    public async Task A_silent_setup_over_a_running_agent_brings_it_back()
    {
        using var install = FakeAgentInstall.Create(UpdateApplyTests.PackId, UpdateApplyTests.InstalledVersion);
        using var runKey = ThrowawayRunKey.Create();
        var feedDir = install.CreateFeedDirectory();

        await using var agent = UpdateApplyTests.LaunchInstalledAgent(install, feedDir, Environment(install, runKey));
        await WaitUntilUpAsync(agent);

        // An Intune install (or anyone) running Setup.exe --silent over the install:
        // Setup stops the agent and, silent, doesn't start it.
        var relaunchPort = TestConfig.FreeLoopbackPort();
        using var setup = Start(
            install.CreateSetupExe(),
            ["--silent", "--installto", install.Root, "--", .. AgentArguments(relaunchPort, feedDir)],
            Environment(install, runKey));
        await WaitForExitAsync(setup, "Setup.exe");

        await agent.WaitForExitAsync(TimeSpan.FromSeconds(10));
        AssertHookRan(install, "--veloapp-install", InstallHookBudget);

        await using var relaunched = await WaitForRelaunchedAgentAsync(install, relaunchPort);
        await AssertStaysUpAsync(install, relaunched);
        await AssertLoggedRelaunchAsync(install, relaunched);
        Assert.Contains($"Anchor.Agent-win-Setup (pid {setup.Id}) won't start the agent again", install.ReadVelopackLog(200));
    }

    [Fact]
    public async Task An_idle_restart_ends_with_the_one_agent_the_updater_started_and_no_relaunch()
    {
        // #456: the idle agent hands the downloaded release to the updater with a
        // restart. The hook runs in that apply too, and must leave the restart to it.
        var feed = fixture.Feed;
        using var install = FakeAgentInstall.Create(UpdateApplyTests.PackId, UpdateApplyTests.InstalledVersion);
        install.RunHooksOnApply();
        using var runKey = ThrowawayRunKey.Create();

        await using var agent = UpdateApplyTests.LaunchInstalledAgent(install, feed.Directory, Environment(install, runKey));
        await UpdateApplyTests.WaitForAgentExitAsync(
            agent, install, UpdateApplyTests.UpdatePassTimeout + UpdateApplyTests.ExitTimeout, "after downloading the update");
        UpdateApplyTests.AssertRestartedIntoUpdate(
            install, agent, feed, await install.WaitForUpdaterInvocationsAsync(UpdateApplyTests.RecordTimeout));

        var hook = AssertHookRan(install, "--veloapp-updated", UpdateHookBudget);
        var starts = install.ReadRecords("update-starts.jsonl");
        Assert.True(starts.Count == 1, $"Expected the updater to start the agent once, got {starts.Count}.");
        Assert.Equal("VELOPACK_RESTART", starts[0].GetProperty("variable").GetString());
        var restartedPid = starts[0].GetProperty("pid").GetInt32();

        // The updater restarts the agent with the arguments it was launched with, so on
        // the same status port.
        await using var restarted = AgentProcess.Attach(Process.GetProcessById(restartedPid), agent.StatusPort, agent.Arguments);
        var up = await restarted.WaitForAsync(_ => true, TimeSpan.FromSeconds(30));
        Assert.True(up is not null, "The agent the updater restarted does not answer on its status port.");

        // Time for a relaunch to show up, if the hook had handed one to a waiter.
        await Task.Delay(NoRelaunchSettle);
        var running = install.RunningAgents();
        try
        {
            Assert.Equal([restartedPid], running.Select(p => p.Id));
        }
        finally
        {
            foreach (var process in running) process.Dispose();
        }
        Assert.DoesNotContain(install.ReadAgentLogLines(), line => line.Contains(RelaunchLogLine));

        // A relaunch would exit at single-instance gating without a log of its own, but
        // not before Velopack's startup in it logs: after the hook, only the updater's
        // restart may have started.
        var velopackLog = install.ReadVelopackLog(400);
        var hookAt = velopackLog.LastIndexOf("Found fast exit hook: --veloapp-updated", StringComparison.Ordinal);
        Assert.True(hookAt >= 0, $"Velopack's log has no update hook:\n{velopackLog}");
        var afterHook = velopackLog[hookAt..];
        var launches = afterHook.Split('\n').Count(line => line.Contains("Starting VelopackApp.Run"));
        Assert.True(launches == 1, $"Expected only the updater's restart to start after the hook, got {launches} launches:\n{afterHook}");
        var runnerPid = hook.GetProperty("runnerPid").GetInt32();
        Assert.Contains($"Update (pid {runnerPid}) starts the agent again itself; not relaunching", velopackLog);
    }

    // The agent's arguments for the updater or Setup.exe to pass on: the e2e's own,
    // and an empty feed, so the agent that comes back finds no update.
    private static string[] AgentArguments(int statusPort, string feedDir) =>
        ["--inject-token", "--status-endpoint", statusPort.ToString(), "--test-update-feed", feedDir];

    // What every process in the chain (agent, updater, hook, waiter, relaunched agent)
    // gets: nothing listening as the backend, the install's own log folder, and the
    // throwaway Run key for the hook.
    private static Dictionary<string, string> Environment(
        FakeAgentInstall install, ThrowawayRunKey runKey, params (string Name, string Value)[] extra)
    {
        var environment = new Dictionary<string, string>
        {
            ["Backend__BaseUrl"] = $"http://127.0.0.1:{TestConfig.FreeLoopbackPort()}",
            ["Dev__ImpersonateOid"] = TestConfig.StudentOid,
            [AgentLogPaths.LogDirectoryEnvVar] = install.LogDir,
            ["ANCHOR_STARTUP_RUN_KEY"] = runKey.RelativePath,
        };
        foreach (var (name, value) in extra) environment[name] = value;
        return environment;
    }

    private static async Task WaitUntilUpAsync(AgentProcess agent)
    {
        var up = await agent.WaitForAsync(s => s.LastUpdateCheck is not null, UpdateApplyTests.UpdatePassTimeout);
        Assert.True(up?.LastUpdateCheck is not null, "The installed agent did not come up and check its (empty) feed.");
    }

    private static Process Start(string exe, IEnumerable<string> args, IReadOnlyDictionary<string, string> environment)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        foreach (var (name, value) in environment) psi.Environment[name] = value;
        return Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {exe}.");
    }

    private static async Task WaitForExitAsync(Process process, string what)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"{what} did not exit within 90 s.");
        }
    }

    // The hook ran once, succeeded and finished well inside Velopack's timeout.
    private static JsonElement AssertHookRan(FakeAgentInstall install, string hook, TimeSpan budget)
    {
        var hooks = install.ReadRecords("update-hooks.jsonl");
        Assert.True(hooks.Count == 1, $"Expected the {hook} hook to run once, got {hooks.Count}. Velopack's log:\n{install.ReadVelopackLog()}");
        var record = hooks[0];
        Assert.Equal(hook, record.GetProperty("hook").GetString());
        Assert.False(record.GetProperty("timedOut").GetBoolean(), $"The {hook} hook ran into Velopack's timeout.");
        Assert.Equal(0, record.GetProperty("exitCode").GetInt32());
        var took = TimeSpan.FromMilliseconds(record.GetProperty("tookMs").GetInt64());
        Assert.True(took <= budget, $"The {hook} hook took {took.TotalSeconds:N1} s; it must stay well inside Velopack's timeout.");
        return record;
    }

    private static async Task<AgentProcess> WaitForRelaunchedAgentAsync(FakeAgentInstall install, int statusPort)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + RelaunchTimeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await http.GetAsync($"http://127.0.0.1:{statusPort}/status");
                if (response.IsSuccessStatusCode) break;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                // Not up yet.
            }
            await Task.Delay(250);
        }

        var running = install.RunningAgents();
        if (running.Count != 1)
        {
            foreach (var process in running) process.Dispose();
            throw new Xunit.Sdk.XunitException(
                $"Expected the agent back within {RelaunchTimeout.TotalSeconds:N0} s, running from {install.CurrentDir}; " +
                $"found {running.Count} agent(s) there.\n" +
                $"Stops: {string.Join("\n  ", install.ReadRecords("update-stops.jsonl"))}\n" +
                $"Velopack's log:\n{install.ReadVelopackLog(80)}");
        }
        return AgentProcess.Attach(running[0], statusPort, []);
    }

    // The agent the waiter started isn't caught by anything the updater does after it:
    // it is still up a while later, and answering.
    private static async Task AssertStaysUpAsync(FakeAgentInstall install, AgentProcess relaunched)
    {
        await Task.Delay(UpdateApplyTests.SettleTime);
        Assert.False(relaunched.HasExited, $"The relaunched agent exited. Velopack's log:\n{install.ReadVelopackLog()}");
        Assert.NotNull(await relaunched.TryGetStatusAsync());
        var running = install.RunningAgents();
        try
        {
            Assert.Equal([relaunched.Pid], running.Select(p => p.Id));
        }
        finally
        {
            foreach (var process in running) process.Dispose();
        }
    }

    // The waiter started it, with the relaunch marker, which the agent logs.
    private static async Task AssertLoggedRelaunchAsync(FakeAgentInstall install, AgentProcess relaunched)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline && !install.ReadAgentLogLines().Any(line => line.Contains(RelaunchLogLine)))
            await Task.Delay(250);
        Assert.True(
            install.ReadAgentLogLines().Any(line => line.Contains(RelaunchLogLine)),
            $"The agent back up (pid {relaunched.Pid}) did not log that the relaunch started it " +
            $"({UpdateRelaunch.RelaunchedArg}).");
    }

    /// <summary>A throwaway HKCU key for the hook's Run entry, removed on dispose.</summary>
    private sealed class ThrowawayRunKey : IDisposable
    {
        private readonly string _root;

        private ThrowawayRunKey(string root)
        {
            _root = root;
            RelativePath = $@"{root}\Run";
        }

        public string RelativePath { get; }

        public static ThrowawayRunKey Create() => new($@"Software\AnchorTests\{Guid.NewGuid():N}");

        public string? ReadCommand()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RelativePath);
            return key?.GetValue(StartupRunKey.ValueName) as string;
        }

        public void Dispose()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false); }
            catch { /* best-effort */ }
        }
    }
}
