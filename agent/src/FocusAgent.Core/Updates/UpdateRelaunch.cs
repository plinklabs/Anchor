using System.Text;

namespace FocusAgent.Core.Updates;

/// <summary>Which of Velopack's lifecycle hooks is running (#487).</summary>
public enum VelopackHookKind
{
    /// <summary><c>--veloapp-install</c>: Setup.exe has just installed this version.</summary>
    Install,

    /// <summary><c>--veloapp-updated</c>: Update.exe has just applied this version.</summary>
    Update,
}

/// <summary>What the install or update hook does about starting the agent again (#487).</summary>
public enum RelaunchDecision
{
    /// <summary>
    /// Nothing starts the agent after this install or update, so the hook hands its
    /// start to a waiter outside the install folder.
    /// </summary>
    Relaunch,

    /// <summary>
    /// Update.exe starts the agent itself once it has applied (an <c>apply</c>
    /// without <c>--norestart</c>): the idle restart (#456) and Velopack's apply at
    /// startup.
    /// </summary>
    UpdaterRestarts,

    /// <summary>A Setup.exe that isn't silent starts the agent itself when it is done.</summary>
    SetupStarts,

    /// <summary>
    /// The hook runs as SYSTEM or in session 0, where no student is signed in: an
    /// agent started from there would run under the wrong account.
    /// </summary>
    NotInUserSession,
}

/// <summary>
/// The outcome of <see cref="UpdateRelaunch.Plan"/>: whether the hook relaunches the
/// agent and, if it does, the arguments the agent gets.
/// </summary>
public sealed record RelaunchPlan(RelaunchDecision Decision, IReadOnlyList<string> AgentArguments)
{
    public bool Relaunches => Decision == RelaunchDecision.Relaunch;
}

/// <summary>
/// Brings the agent back after an update or install that Velopack applies without
/// starting the agent again (#487).
///
/// <para>
/// Two paths leave a student without the agent. An agent older than 0.4.0 ends its
/// update pass by starting Update.exe with <c>--norestart</c>: Update.exe waits 60 s,
/// kills the agent, applies and leaves it closed. A silent Setup.exe over a running
/// install (an Intune install, say) stops the agent and doesn't start it when silent.
/// Both run the hook of the version being installed, so the fix lives there: it also
/// covers updates that older agents start.
/// </para>
///
/// <para>
/// The hook can't start the agent itself: right after every hook, Velopack stops every
/// process whose exe is in the install folder. So it starts a waiter from System32
/// (<see cref="WaiterScript"/>, a hidden Windows PowerShell), which waits for the
/// updater or installer (the hook's parent process) to exit and then starts the agent
/// from <c>current\</c>, with <see cref="RelaunchedArg"/> for the log.
/// </para>
///
/// <para>
/// Whether anything else starts the agent is read from the parent's own command line,
/// the arguments Velopack's library or the person running Setup.exe gave it. Update.exe
/// restarts the agent unless <c>--norestart</c> is there; Setup.exe starts it unless
/// <c>--silent</c> (<c>-s</c>) is. When the command line can't be read, the hook
/// relaunches: a second agent exits quietly at single-instance gating, while no agent
/// is the bug.
/// </para>
/// </summary>
public static class UpdateRelaunch
{
    /// <summary>
    /// Marks a launch the waiter started, only for the agent's log. Not passed on when
    /// that agent later restarts into an update (see <see cref="WithoutRelaunchedArg"/>).
    /// </summary>
    public const string RelaunchedArg = "--relaunched-after-update";

    /// <summary>How long the waiter waits for the updater or installer to exit before it gives up.</summary>
    public static readonly TimeSpan WaitBudget = TimeSpan.FromMinutes(2);

    /// <summary>The agent exe the waiter starts (<c>current\FocusAgent.App.exe</c>).</summary>
    public const string ExeEnvVar = "ANCHOR_RELAUNCH_EXE";

    /// <summary>The agent's arguments, as one command-line string (<see cref="JoinArguments"/>).</summary>
    public const string ArgumentsEnvVar = "ANCHOR_RELAUNCH_ARGS";

    /// <summary>The id of the updater or installer to wait for.</summary>
    public const string WaitPidEnvVar = "ANCHOR_RELAUNCH_WAIT_PID";

    /// <summary>
    /// The process name of the updater or installer (empty when unknown), so a process
    /// that took over its id after it exited isn't waited for.
    /// </summary>
    public const string WaitNameEnvVar = "ANCHOR_RELAUNCH_WAIT_NAME";

    /// <summary>
    /// The waiter: a Windows PowerShell command run hidden from System32, outside the
    /// install folder. It reads what to wait for and what to start from the
    /// environment (<see cref="ExeEnvVar"/> and the others), so the command never
    /// carries a path or an argument and needs no quoting. It uses cmdlets only, which
    /// run under Constrained Language Mode. It waits up to <see cref="WaitBudget"/> for
    /// the updater or installer to exit and gives up (exit code 2) if it hasn't, then
    /// starts the agent the way Windows does at sign-in, plus the arguments it was given.
    /// </summary>
    public static readonly string WaiterScript = string.Join(
        " ",
        $"$exe = $env:{ExeEnvVar};",
        $"$arguments = $env:{ArgumentsEnvVar};",
        $"$waitId = $env:{WaitPidEnvVar};",
        $"$waitName = $env:{WaitNameEnvVar};",
        "Remove-Item -Path Env:ANCHOR_RELAUNCH_* -ErrorAction SilentlyContinue;",
        "$waitFor = Get-Process -Id $waitId -ErrorAction SilentlyContinue |",
        "Where-Object { -not $waitName -or $_.ProcessName -eq $waitName };",
        "if ($waitFor) {",
        $"$waitFor | Wait-Process -Timeout {(int)WaitBudget.TotalSeconds} -ErrorAction SilentlyContinue;",
        "if (Get-Process -Id $waitId -ErrorAction SilentlyContinue |",
        "Where-Object { -not $waitName -or $_.ProcessName -eq $waitName }) { exit 2 }",
        "};",
        "Start-Process -FilePath $exe -ArgumentList $arguments",
        "-WorkingDirectory (Split-Path -Parent $exe) -ErrorAction Stop");

    /// <summary>
    /// Decide what the <paramref name="hook"/> does, from the command line of the
    /// process that runs it (<paramref name="parentCommandLine"/>, null when it can't
    /// be read) and who runs it. The agent gets the arguments the parent was told to
    /// start it with (everything after <c>--</c>: none for an update from an older agent
    /// or a plain silent Setup.exe, which is how Windows starts it at sign-in) plus
    /// <see cref="RelaunchedArg"/>.
    /// </summary>
    public static RelaunchPlan Plan(
        VelopackHookKind hook, string? parentCommandLine, bool runsAsSystem, int sessionId)
    {
        if (runsAsSystem || sessionId == 0)
            return new RelaunchPlan(RelaunchDecision.NotInUserSession, []);

        IReadOnlyList<string> passOn = [];
        if (parentCommandLine is not null)
        {
            var args = SplitCommandLine(parentCommandLine).Skip(1).ToList();
            var separator = args.IndexOf("--");
            var options = separator < 0 ? args : args.Take(separator).ToList();
            passOn = separator < 0 ? [] : args.Skip(separator + 1).ToList();

            if (hook == VelopackHookKind.Update && HasOption(options, "apply") && !HasOption(options, "--norestart"))
                return new RelaunchPlan(RelaunchDecision.UpdaterRestarts, []);
            if (hook == VelopackHookKind.Install && !IsSilent(options))
                return new RelaunchPlan(RelaunchDecision.SetupStarts, []);
        }

        return new RelaunchPlan(RelaunchDecision.Relaunch, [.. WithoutRelaunchedArg(passOn), RelaunchedArg]);
    }

    /// <summary>
    /// <paramref name="args"/> without <see cref="RelaunchedArg"/>: the arguments an
    /// agent the waiter started passes on when it restarts into an update (#456), so
    /// that restart isn't logged as a relaunch.
    /// </summary>
    public static IReadOnlyList<string> WithoutRelaunchedArg(IEnumerable<string> args) =>
        args.Where(a => !string.Equals(a, RelaunchedArg, StringComparison.OrdinalIgnoreCase)).ToList();

    // Setup.exe's -s / --silent (clap also accepts it among other short flags: -sv).
    private static bool IsSilent(IReadOnlyList<string> options) =>
        options.Any(o =>
            string.Equals(o, "--silent", StringComparison.OrdinalIgnoreCase)
            || (o.Length > 1 && o[0] == '-' && o[1] != '-' && o.Skip(1).All(char.IsAsciiLetter) && o.Contains('s')));

    private static bool HasOption(IReadOnlyList<string> options, string option) =>
        options.Any(o => string.Equals(o, option, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Split a Windows command line into its arguments, the program first, by the
    /// rules the C runtime and <c>CommandLineToArgvW</c> use (and Velopack quotes by):
    /// the program runs to the next quote when quoted, else to the first blank;
    /// after it, blanks separate arguments outside quotes, 2n backslashes before a
    /// quote are n backslashes and the quote opens or closes quoting, 2n+1 are n and a
    /// literal quote, and <c>""</c> inside quotes is a literal quote.
    /// </summary>
    public static IReadOnlyList<string> SplitCommandLine(string commandLine)
    {
        var result = new List<string>();
        var i = 0;
        var n = commandLine.Length;

        // The program: no escapes.
        while (i < n && IsBlank(commandLine[i])) i++;
        if (i >= n) return result;
        var program = new StringBuilder();
        if (commandLine[i] == '"')
        {
            i++;
            while (i < n && commandLine[i] != '"') program.Append(commandLine[i++]);
            if (i < n) i++; // the closing quote
        }
        while (i < n && !IsBlank(commandLine[i])) program.Append(commandLine[i++]);
        result.Add(program.ToString());

        while (true)
        {
            while (i < n && IsBlank(commandLine[i])) i++;
            if (i >= n) break;

            var arg = new StringBuilder();
            var quoted = false;
            while (i < n && (quoted || !IsBlank(commandLine[i])))
            {
                var c = commandLine[i];
                if (c == '\\')
                {
                    var backslashes = 0;
                    while (i < n && commandLine[i] == '\\')
                    {
                        backslashes++;
                        i++;
                    }
                    if (i < n && commandLine[i] == '"')
                    {
                        arg.Append('\\', backslashes / 2);
                        if (backslashes % 2 == 1)
                        {
                            arg.Append('"');
                            i++;
                        }
                    }
                    else
                    {
                        arg.Append('\\', backslashes);
                    }
                }
                else if (c == '"')
                {
                    if (quoted && i + 1 < n && commandLine[i + 1] == '"')
                    {
                        arg.Append('"');
                        i += 2;
                    }
                    else
                    {
                        quoted = !quoted;
                        i++;
                    }
                }
                else
                {
                    arg.Append(c);
                    i++;
                }
            }
            result.Add(arg.ToString());
        }
        return result;
    }

    /// <summary>
    /// Join <paramref name="args"/> into one command-line string that
    /// <see cref="SplitCommandLine"/> (and so the agent's own argument parsing) splits
    /// back into the same arguments: an argument with a blank or a quote in it, or an
    /// empty one, is quoted, with backslashes doubled before a quote.
    /// </summary>
    public static string JoinArguments(IEnumerable<string> args)
    {
        var line = new StringBuilder();
        foreach (var arg in args)
        {
            if (line.Length > 0) line.Append(' ');
            if (arg.Length > 0 && !arg.Any(c => IsBlank(c) || c == '"'))
            {
                line.Append(arg);
                continue;
            }

            line.Append('"');
            var backslashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                {
                    line.Append('\\', backslashes * 2 + 1).Append('"');
                }
                else
                {
                    line.Append('\\', backslashes).Append(c);
                }
                backslashes = 0;
            }
            line.Append('\\', backslashes * 2).Append('"');
        }
        return line.ToString();
    }

    private static bool IsBlank(char c) => c is ' ' or '\t' or '\n' or '\v';
}
