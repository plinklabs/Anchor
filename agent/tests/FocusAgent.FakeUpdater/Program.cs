using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// The update e2e's stand-in for Velopack's Update.exe (see the .csproj). Appends the
// arguments it was started with, as one JSON array per line, to
// update-invocations.jsonl beside itself. Then, like the real updater, it waits for
// the --waitPid process to exit, for at most the real updater's 60 s, and appends
// whether that process exited on its own (#456) to update-waits.jsonl. Where the
// real updater would kill the process after 60 s, this one only records it. It
// applies nothing.
//
// #487: in an install whose root holds an "apply-runs-hooks" file, an apply goes on
// the way Velopack 1.2.0's really does, minus the file swap: it stops every process
// running from the install folder, runs the new version's --veloapp-updated hook
// (15 s at most), stops every process in the install folder again ("in case the hook
// left running processes"), and starts the agent again with VELOPACK_RESTART set,
// unless --norestart. So the spec sees what survives the updater: an agent the hook
// started from the install folder would not.
//
// Run under any other file name (the spec copies it as Anchor.Agent-win-Setup.exe),
// it is Velopack's Setup.exe over an existing install (#487): it stops the install
// in --installto, runs --veloapp-install (30 s at most), stops the install folder
// again, and starts the agent only when not --silent, as Setup.exe does.
var selfName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "Update");
if (!string.Equals(selfName, "Update", StringComparison.OrdinalIgnoreCase))
    return Velopack.Setup(args);

var root = AppContext.BaseDirectory.TrimEnd('\\');
Records.Append(root, "update-invocations.jsonl", JsonSerializer.Serialize(args));
var runsHooks = File.Exists(Path.Combine(root, "apply-runs-hooks"));

var waitPid = Args.ValueAfter(args, "--waitPid");
if (waitPid is not null && int.TryParse(waitPid, out var pid))
{
    var waitTimeout = runsHooks
        && int.TryParse(Environment.GetEnvironmentVariable("ANCHOR_FAKE_UPDATER_WAIT_SECONDS"), out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(60);
    var waited = Stopwatch.StartNew();
    var exitedOnItsOwn = WaitForExit(pid, waitTimeout);
    Records.Append(root, "update-waits.jsonl", JsonSerializer.Serialize(new
    {
        waitPid = pid,
        exitedOnItsOwn,
        waitedMs = (long)waited.Elapsed.TotalMilliseconds,
    }));
}

if (runsHooks && Args.Options(args).Contains("apply"))
    Velopack.Apply(args, root);
return 0;

static bool WaitForExit(int pid, TimeSpan timeout)
{
    try
    {
        using var process = Process.GetProcessById(pid);
        return process.WaitForExit(timeout);
    }
    catch (ArgumentException)
    {
        // Already gone.
        return true;
    }
}

/// <summary>What Velopack's Update.exe apply and Setup.exe do around the app's hooks (#487).</summary>
internal static class Velopack
{
    private const string AgentExeName = "FocusAgent.App.exe";

    public static void Apply(string[] args, string root)
    {
        // Before it swaps versions, the updater stops whatever still runs from the
        // install folder: the agent it waited for, when that didn't exit.
        ForceStop(root, "before applying");
        RunHook(root, "--veloapp-updated", TimeSpan.FromSeconds(15));
        if (!Args.Options(args).Contains("--norestart"))
            StartAgent(root, Args.AfterSeparator(args), "VELOPACK_RESTART");
    }

    public static int Setup(string[] args)
    {
        var root = Args.ValueAfter(args, "--installto") ?? Args.ValueAfter(args, "-t");
        if (string.IsNullOrEmpty(root))
            return 98;
        root = Path.GetFullPath(root).TrimEnd('\\');
        Records.Append(root, "setup-invocations.jsonl", JsonSerializer.Serialize(args));

        // An existing install is stopped and replaced; the replacing is left out.
        ForceStop(root, "before installing");
        RunHook(root, "--veloapp-install", TimeSpan.FromSeconds(30));
        var silent = Args.Options(args).Any(o => o is "--silent" or "-s");
        if (!silent)
            StartAgent(root, Args.AfterSeparator(args), "VELOPACK_FIRSTRUN");
        return 0;
    }

    // run_hook: the main exe with the hook and the version, from current\, waited on
    // for the hook's time; then everything in the install folder is stopped.
    private static void RunHook(string root, string hook, TimeSpan timeout)
    {
        var current = Path.Combine(root, "current");
        var psi = new ProcessStartInfo(Path.Combine(current, AgentExeName))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = current,
        };
        psi.ArgumentList.Add(hook);
        psi.ArgumentList.Add(InstalledVersion(current));

        var took = Stopwatch.StartNew();
        int? exitCode = null;
        var timedOut = false;
        using (var process = Process.Start(psi))
        {
            if (process is not null)
            {
                if (process.WaitForExit(timeout))
                {
                    exitCode = process.ExitCode;
                }
                else
                {
                    timedOut = true;
                    try { process.Kill(); } catch { }
                }
            }
        }
        Records.Append(root, "update-hooks.jsonl", JsonSerializer.Serialize(new
        {
            hook,
            // The hook's parent: what its log lines name.
            runnerPid = Environment.ProcessId,
            exitCode,
            timedOut,
            tookMs = (long)took.Elapsed.TotalMilliseconds,
        }));

        ForceStop(root, $"after {hook}");
    }

    private static void StartAgent(string root, IReadOnlyList<string> agentArgs, string variable)
    {
        var current = Path.Combine(root, "current");
        var psi = new ProcessStartInfo(Path.Combine(current, AgentExeName))
        {
            UseShellExecute = false,
            WorkingDirectory = current,
        };
        foreach (var arg in agentArgs) psi.ArgumentList.Add(arg);
        psi.Environment[variable] = "true";
        using var agent = Process.Start(psi);
        Records.Append(root, "update-starts.jsonl", JsonSerializer.Serialize(new { pid = agent?.Id, variable }));
    }

    // force_stop_package: kill every process whose exe is in the install folder,
    // except this one.
    private static void ForceStop(string root, string when)
    {
        var prefix = root + "\\";
        var killed = new List<object>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                var path = ImagePath(process.Id);
                if (path is null || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    process.Kill();
                    process.WaitForExit(5000);
                    killed.Add(new { pid = process.Id, path });
                }
                catch
                {
                    // Gone already.
                }
            }
        }
        Records.Append(root, "update-stops.jsonl", JsonSerializer.Serialize(new { when, killed }));
    }

    private static string InstalledVersion(string current)
    {
        try
        {
            var match = Regex.Match(File.ReadAllText(Path.Combine(current, "sq.version")), "<version>([^<]+)</version>");
            if (match.Success) return match.Groups[1].Value;
        }
        catch
        {
            // Fall through.
        }
        return "0.0.0";
    }

    private static string? ImagePath(int pid)
    {
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, (uint)pid);
        if (handle == 0) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString(0, (int)size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(nint process, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}

internal static class Args
{
    public static string? ValueAfter(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == flag) return args[i + 1];
        return null;
    }

    // The updater's or installer's own options: everything before "--".
    public static IReadOnlyList<string> Options(string[] args)
    {
        var separator = Array.IndexOf(args, "--");
        return separator < 0 ? args : args[..separator];
    }

    // The arguments it passes on to the agent it starts: everything after "--".
    public static IReadOnlyList<string> AfterSeparator(string[] args)
    {
        var separator = Array.IndexOf(args, "--");
        return separator < 0 ? [] : args[(separator + 1)..];
    }
}

internal static class Records
{
    public static void Append(string folder, string fileName, string line)
    {
        var path = Path.Combine(folder, fileName);
        // Two launches could race on the file; the loser retries briefly.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.AppendAllText(path, line + Environment.NewLine);
                return;
            }
            catch (IOException) when (attempt < 40)
            {
                Thread.Sleep(50);
            }
        }
    }
}
