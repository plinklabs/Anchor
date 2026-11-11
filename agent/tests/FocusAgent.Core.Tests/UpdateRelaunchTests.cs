using FocusAgent.Core.Updates;

namespace FocusAgent.Core.Tests;

/// <summary>
/// #487: what the Velopack install and update hooks decide about starting the agent
/// again, from the command line of the updater or installer that runs them. The
/// command lines are the ones Velopack's library (1.2.0, what every agent release
/// ships) and Setup.exe really get.
/// </summary>
public class UpdateRelaunchTests
{
    private const string UpdateExe = @"C:\Users\Student One\AppData\Local\Anchor.Agent\Update.exe";
    private const string Packages = @"C:\Users\Student One\AppData\Local\Anchor.Agent\packages";
    private const string Root = @"C:\Users\Student One\AppData\Local\Anchor.Agent";

    // What an agent before 0.4.0 starts at the end of its update pass:
    // WaitExitThenApplyUpdates(silent: true, restart: false) through UpdateExe.Apply.
    private static readonly string OldAgentApply =
        $"\"{UpdateExe}\" --silent apply --package \"{Packages}\\Anchor.Agent-0.4.1-full.nupkg\" " +
        $"--waitPid 4242 --norestart --rootDir \"{Root}\" --packageDir \"{Packages}\"";

    // The idle restart (#456): WaitExitThenApplyUpdates(silent: true, restart: true, args).
    private static readonly string IdleRestartApply =
        $"\"{UpdateExe}\" --silent apply --package \"{Packages}\\Anchor.Agent-0.4.1-full.nupkg\" " +
        $"--waitPid 4242 --rootDir \"{Root}\" --packageDir \"{Packages}\" -- --inject-token";

    // VelopackApp's apply at startup: UpdateExe.Apply(silent: false, restart: true, args).
    private static readonly string StartupApply =
        $"\"{UpdateExe}\" apply --package \"{Packages}\\Anchor.Agent-0.4.1-full.nupkg\" " +
        $"--waitPid 4242 --rootDir \"{Root}\" --packageDir \"{Packages}\"";

    [Fact]
    public void An_update_an_older_agent_started_without_a_restart_relaunches_the_agent_as_at_sign_in()
    {
        var plan = UpdateRelaunch.Plan(VelopackHookKind.Update, OldAgentApply, runsAsSystem: false, sessionId: 1);

        Assert.Equal(RelaunchDecision.Relaunch, plan.Decision);
        Assert.True(plan.Relaunches);
        // No arguments at all at sign-in (the Run entry passes none), plus the log marker.
        Assert.Equal([UpdateRelaunch.RelaunchedArg], plan.AgentArguments);
    }

    [Theory]
    [MemberData(nameof(RestartingApplies))]
    public void An_update_the_updater_restarts_the_agent_after_is_not_relaunched(string commandLine)
    {
        var plan = UpdateRelaunch.Plan(VelopackHookKind.Update, commandLine, runsAsSystem: false, sessionId: 1);

        Assert.Equal(RelaunchDecision.UpdaterRestarts, plan.Decision);
        Assert.False(plan.Relaunches);
        Assert.Empty(plan.AgentArguments);
    }

    public static TheoryData<string> RestartingApplies() => new() { IdleRestartApply, StartupApply };

    [Fact]
    public void A_norestart_among_the_restart_arguments_is_an_argument_for_the_agent_not_for_the_updater()
    {
        var plan = UpdateRelaunch.Plan(
            VelopackHookKind.Update, $"\"{UpdateExe}\" apply --waitPid 1 -- --norestart", runsAsSystem: false, sessionId: 1);

        Assert.Equal(RelaunchDecision.UpdaterRestarts, plan.Decision);
    }

    [Fact]
    public void The_relaunch_passes_on_the_arguments_the_updater_was_given_for_the_agent()
    {
        var plan = UpdateRelaunch.Plan(
            VelopackHookKind.Update,
            $"\"{UpdateExe}\" apply --norestart --waitPid 1 -- --status-endpoint 5295 --test-update-feed \"C:\\a feed\"",
            runsAsSystem: false,
            sessionId: 1);

        Assert.Equal(
            ["--status-endpoint", "5295", "--test-update-feed", @"C:\a feed", UpdateRelaunch.RelaunchedArg],
            plan.AgentArguments);
    }

    [Theory]
    [InlineData("--silent")]
    [InlineData("-s")]
    [InlineData("-vs")]
    [InlineData("--SILENT")]
    public void A_silent_setup_over_the_agent_relaunches_it(string silentFlag)
    {
        var plan = UpdateRelaunch.Plan(
            VelopackHookKind.Install,
            $"\"C:\\IME\\Anchor.Agent-win-Setup.exe\" {silentFlag} --log \"C:\\logs\\intune-setup.log\"",
            runsAsSystem: false,
            sessionId: 2);

        Assert.Equal(RelaunchDecision.Relaunch, plan.Decision);
        Assert.Equal([UpdateRelaunch.RelaunchedArg], plan.AgentArguments);
    }

    [Theory]
    [InlineData("\"C:\\Downloads\\Anchor.Agent-win-Setup.exe\"")]
    [InlineData("\"C:\\Downloads\\Anchor.Agent-win-Setup.exe\" --log C:\\logs\\setup.log -- -s")]
    public void A_setup_that_isnt_silent_starts_the_agent_itself(string commandLine)
    {
        var plan = UpdateRelaunch.Plan(VelopackHookKind.Install, commandLine, runsAsSystem: false, sessionId: 1);

        Assert.Equal(RelaunchDecision.SetupStarts, plan.Decision);
        Assert.False(plan.Relaunches);
    }

    [Theory]
    [InlineData(VelopackHookKind.Update)]
    [InlineData(VelopackHookKind.Install)]
    public void When_the_parents_command_line_cant_be_read_the_hook_relaunches(VelopackHookKind hook)
    {
        // A second agent exits quietly at single-instance gating; no agent is the bug.
        var plan = UpdateRelaunch.Plan(hook, parentCommandLine: null, runsAsSystem: false, sessionId: 1);

        Assert.Equal(RelaunchDecision.Relaunch, plan.Decision);
        Assert.Equal([UpdateRelaunch.RelaunchedArg], plan.AgentArguments);
    }

    [Theory]
    [InlineData(VelopackHookKind.Update, true, 1)]
    [InlineData(VelopackHookKind.Update, false, 0)]
    [InlineData(VelopackHookKind.Install, true, 0)]
    [InlineData(VelopackHookKind.Install, true, 1)]
    public void Nothing_starts_as_SYSTEM_or_outside_a_users_session(VelopackHookKind hook, bool runsAsSystem, int sessionId)
    {
        var commandLine = hook == VelopackHookKind.Update ? OldAgentApply : "\"C:\\IME\\Setup.exe\" --silent";

        var plan = UpdateRelaunch.Plan(hook, commandLine, runsAsSystem, sessionId);

        Assert.Equal(RelaunchDecision.NotInUserSession, plan.Decision);
        Assert.False(plan.Relaunches);
    }

    [Fact]
    public void The_relaunch_marker_is_not_doubled()
    {
        var plan = UpdateRelaunch.Plan(
            VelopackHookKind.Update,
            $"\"{UpdateExe}\" apply --norestart -- {UpdateRelaunch.RelaunchedArg}",
            runsAsSystem: false,
            sessionId: 1);

        Assert.Equal([UpdateRelaunch.RelaunchedArg], plan.AgentArguments);
    }

    [Fact]
    public void A_relaunched_agent_restarts_into_an_update_without_the_marker()
    {
        Assert.Equal(
            ["--status-endpoint", "5295"],
            UpdateRelaunch.WithoutRelaunchedArg(["--status-endpoint", UpdateRelaunch.RelaunchedArg, "5295"]));
    }

    [Theory]
    [InlineData("\"C:\\a b\\Update.exe\" apply --x", new[] { @"C:\a b\Update.exe", "apply", "--x" })]
    [InlineData("C:\\Update.exe  a\t b ", new[] { @"C:\Update.exe", "a", "b" })]
    [InlineData("x \"a b\" \"\" c", new[] { "x", "a b", "", "c" })]
    [InlineData("x \"C:\\dir\\\\\" next", new[] { "x", @"C:\dir\", "next" })]
    [InlineData("x a\\\"b \"c\"\"d\"", new[] { "x", "a\"b", "c\"d" })]
    [InlineData("x a\\\\b \\\\\\\"q", new[] { "x", @"a\\b", "\\\"q" })]
    [InlineData("", new string[0])]
    public void SplitCommandLine_follows_the_C_runtime_rules(string commandLine, string[] expected)
    {
        Assert.Equal(expected, UpdateRelaunch.SplitCommandLine(commandLine));
    }

    [Theory]
    [InlineData(new[] { "--relaunched-after-update" }, "--relaunched-after-update")]
    [InlineData(new[] { "--feed", @"C:\a b\feed" }, "--feed \"C:\\a b\\feed\"")]
    [InlineData(new[] { @"C:\dir\", "" }, "C:\\dir\\ \"\"")]
    [InlineData(new[] { @"C:\a b\" }, "\"C:\\a b\\\\\"")]
    [InlineData(new[] { "say \"hi\"" }, "\"say \\\"hi\\\"\"")]
    public void JoinArguments_quotes_what_needs_quoting(string[] args, string expected)
    {
        Assert.Equal(expected, UpdateRelaunch.JoinArguments(args));
    }

    [Theory]
    [MemberData(nameof(RoundTrips))]
    public void JoinArguments_splits_back_into_the_same_arguments(string[] args)
    {
        var line = "agent.exe " + UpdateRelaunch.JoinArguments(args);

        Assert.Equal(args, UpdateRelaunch.SplitCommandLine(line).Skip(1));
    }

    public static TheoryData<string[]> RoundTrips() => new()
    {
        new[] { "--relaunched-after-update" },
        new[] { @"C:\Users\Student One\feed", "--x", "" },
        new[] { @"C:\trailing\", @"C:\space trailing\", "a\"b", "\\\"", "\\\\" },
    };

    [Fact]
    public void The_waiter_carries_no_paths_and_no_double_quotes_and_waits_two_minutes()
    {
        // Everything it needs comes through the environment, so nothing in the command
        // depends on how a path or an argument is quoted on its way to powershell.exe.
        Assert.DoesNotContain("\"", UpdateRelaunch.WaiterScript);
        Assert.DoesNotContain("\n", UpdateRelaunch.WaiterScript);
        Assert.Contains($"$env:{UpdateRelaunch.ExeEnvVar}", UpdateRelaunch.WaiterScript);
        Assert.Contains($"$env:{UpdateRelaunch.ArgumentsEnvVar}", UpdateRelaunch.WaiterScript);
        Assert.Contains($"$env:{UpdateRelaunch.WaitPidEnvVar}", UpdateRelaunch.WaiterScript);
        Assert.Contains($"$env:{UpdateRelaunch.WaitNameEnvVar}", UpdateRelaunch.WaiterScript);
        Assert.Contains("Wait-Process -Timeout 120", UpdateRelaunch.WaiterScript);
        Assert.Equal(TimeSpan.FromMinutes(2), UpdateRelaunch.WaitBudget);
        // Its variables don't reach the agent it starts.
        Assert.Contains("Remove-Item -Path Env:ANCHOR_RELAUNCH_*", UpdateRelaunch.WaiterScript);
        Assert.All(
            new[] { UpdateRelaunch.ExeEnvVar, UpdateRelaunch.ArgumentsEnvVar, UpdateRelaunch.WaitPidEnvVar, UpdateRelaunch.WaitNameEnvVar },
            name => Assert.StartsWith("ANCHOR_RELAUNCH_", name));
    }
}
