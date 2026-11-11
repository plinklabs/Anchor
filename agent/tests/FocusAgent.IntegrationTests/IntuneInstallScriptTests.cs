using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using FocusAgent.Core.Startup;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// End-to-end proof for #457: the Intune install script
/// (<c>agent/scripts/release-assets/Install-AnchorAgent.ps1</c>), run the way Intune
/// runs a Win32 app's install command: <c>powershell.exe -ExecutionPolicy Bypass
/// -File</c> from the unpacked package, with the release's
/// <c>Anchor.Agent-win-Setup.exe</c> beside it, in 64-bit and in 32-bit PowerShell
/// (the Intune Management Extension is a 32-bit process).
///
/// <para>
/// Setup.exe is a recording stand-in (FocusAgent.FakeSetup) that "installs" a
/// stand-in agent into a temp install folder and writes its Run value into a
/// throwaway HKCU key, so no spec installs anything into the profile of the user
/// running the tests. Each run happens inside a job object, and the spec waits for
/// the whole job to empty: that is an installer host that waits for everything the
/// install command leaves running, the strictest thing the Intune Management
/// Extension could do. The agent the script starts must keep running outside it.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class IntuneInstallScriptTests
{
    [Theory]
    [InlineData(PowerShellHost.X64)]
    [InlineData(PowerShellHost.X86)]
    public async Task Not_installed_installs_silently_and_starts_the_agent_outside_the_install(PowerShellHost host)
    {
        using var sandbox = IntuneSandbox.Create();

        var run = await sandbox.RunScriptAsync(host);

        Assert.True(run.ExitCode == 0, run.Describe());
        // The install command, and everything it started, is done: an installer
        // host waiting on all of it isn't held up by the agent left running.
        Assert.True(run.ProcessTreeExited, "The script's process tree never emptied.\n" + run.Describe());

        var setup = Assert.Single(sandbox.ReadSetupInvocations());
        Assert.Contains("--silent", setup);
        Assert.Equal(sandbox.SetupLogPath, ValueAfter(setup, "--log"));

        // Started once, with the sign-in command (the quoted exe, no arguments), and
        // still running now that the install is over.
        var start = Assert.Single(sandbox.ReadAgentStarts());
        Assert.Empty(start.Args);
        Assert.Equal([start.Pid], sandbox.RunningAgentPids());
        // As the signed-in user, on their desktop: where the tray icon shows.
        using (var self = Process.GetCurrentProcess())
            Assert.Equal(self.SessionId, start.SessionId);
        Assert.Equal(DesktopNames.WindowStation(), start.WindowStation);
        Assert.Equal(DesktopNames.Desktop(), start.Desktop);

        var log = sandbox.ReadLog();
        Assert.Contains("Started the agent through WMI", log);
        Assert.Contains($"The agent is running (PID {start.Pid})", log);
    }

    [Fact]
    public async Task Installed_and_running_agent_is_not_reinstalled_or_started_again()
    {
        using var sandbox = IntuneSandbox.Create();
        sandbox.InstallAgent(withRunValue: true);
        var agentPid = await sandbox.StartAgentAsync();

        var run = await sandbox.RunScriptAsync(PowerShellHost.X86);

        Assert.True(run.ExitCode == 0, run.Describe());
        // No Setup.exe: over an installed agent it would stop it, mid-lesson.
        Assert.Empty(sandbox.ReadSetupInvocations());
        // The running agent is untouched, and no second one was started.
        Assert.Equal([agentPid], sandbox.ReadAgentStarts().Select(s => s.Pid));
        Assert.Equal([agentPid], sandbox.RunningAgentPids());
        var log = sandbox.ReadLog();
        Assert.Contains("The agent is already installed", log);
        Assert.Contains($"The agent is already running (PID {agentPid})", log);
    }

    [Fact]
    public async Task Installed_agent_that_is_not_running_is_started_without_setup()
    {
        using var sandbox = IntuneSandbox.Create();
        sandbox.InstallAgent(withRunValue: true);

        var run = await sandbox.RunScriptAsync(PowerShellHost.X86);

        Assert.True(run.ExitCode == 0, run.Describe());
        Assert.True(run.ProcessTreeExited, "The script's process tree never emptied.\n" + run.Describe());
        Assert.Empty(sandbox.ReadSetupInvocations());
        var start = Assert.Single(sandbox.ReadAgentStarts());
        Assert.Equal([start.Pid], sandbox.RunningAgentPids());
    }

    [Theory]
    [InlineData(1)] // Velopack's Setup exits with 1 on any failure.
    [InlineData(42)]
    public async Task Failed_setup_exits_with_its_code_and_the_log_says_why(int setupExitCode)
    {
        using var sandbox = IntuneSandbox.Create();
        const string reason = "Unable to extract the package: there is not enough space on the disk.";

        var run = await sandbox.RunScriptAsync(PowerShellHost.X86, new Dictionary<string, string>
        {
            ["ANCHOR_FAKE_SETUP_EXIT_CODE"] = setupExitCode.ToString(),
            ["ANCHOR_FAKE_SETUP_ERROR"] = reason,
        });

        Assert.True(run.ExitCode == setupExitCode, run.Describe());
        var log = sandbox.ReadLog();
        Assert.Contains($"Setup.exe failed with exit code {setupExitCode}", log);
        // The reason from Setup.exe's own log, copied into the script's log.
        Assert.Contains(reason, log);
        Assert.Empty(sandbox.ReadAgentStarts());
        Assert.Empty(sandbox.RunningAgentPids());
    }

    [Fact]
    public async Task Missing_setup_exits_with_10()
    {
        using var sandbox = IntuneSandbox.Create();
        File.Delete(sandbox.SetupExe);

        var run = await sandbox.RunScriptAsync(PowerShellHost.X86);

        Assert.True(run.ExitCode == 10, run.Describe());
        Assert.Contains("Anchor.Agent-win-Setup.exe not found", sandbox.ReadLog());
    }

    [Fact]
    public async Task Setup_that_installs_nothing_exits_with_11()
    {
        using var sandbox = IntuneSandbox.Create();

        var run = await sandbox.RunScriptAsync(PowerShellHost.X86, new Dictionary<string, string>
        {
            ["ANCHOR_FAKE_SETUP_SKIP_INSTALL"] = "1",
        });

        Assert.True(run.ExitCode == 11, run.Describe());
        Assert.Single(sandbox.ReadSetupInvocations());
        Assert.Contains("Setup.exe exited with 0, but the agent isn't installed", sandbox.ReadLog());
    }

    [Fact]
    public async Task Installed_agent_without_a_run_value_exits_with_12()
    {
        using var sandbox = IntuneSandbox.Create();
        sandbox.InstallAgent(withRunValue: false);

        var run = await sandbox.RunScriptAsync(PowerShellHost.X86);

        Assert.True(run.ExitCode == 12, run.Describe());
        Assert.Contains($"There is no {StartupRunKey.ValueName} value", sandbox.ReadLog());
        Assert.Empty(sandbox.ReadAgentStarts());
    }

    [Fact]
    public async Task Run_value_that_cannot_be_started_exits_with_14()
    {
        using var sandbox = IntuneSandbox.Create();
        sandbox.InstallAgent(withRunValue: false);
        var missing = Path.Combine(sandbox.Root, "gone", "FocusAgent.App.exe");
        sandbox.WriteRunValue(StartupRunKey.CommandFor(missing));

        var run = await sandbox.RunScriptAsync(PowerShellHost.X86);

        Assert.True(run.ExitCode == 14, run.Describe());
        var log = sandbox.ReadLog();
        Assert.Contains($"The sign-in command starts {missing}", log);
        Assert.Contains("Could not start the agent", log);
        Assert.Empty(sandbox.ReadAgentStarts());
    }

    [Fact]
    public async Task Agent_that_stops_right_after_its_start_exits_with_14()
    {
        using var sandbox = IntuneSandbox.Create();
        sandbox.InstallAgent(withRunValue: true);
        sandbox.MakeAgentExitAtStart();

        var run = await sandbox.RunScriptAsync(PowerShellHost.X86);

        Assert.True(run.ExitCode == 14, run.Describe());
        Assert.Single(sandbox.ReadAgentStarts());
        Assert.Contains("The agent stopped within", sandbox.ReadLog());
    }

    private static string? ValueAfter(string[] args, string flag)
    {
        var i = Array.IndexOf(args, flag);
        return i >= 0 && i < args.Length - 1 ? args[i + 1] : null;
    }
}

/// <summary>Which Windows PowerShell runs the script.</summary>
public enum PowerShellHost
{
    X64,
    /// <summary>What a 32-bit process such as the Intune Management Extension starts as powershell.exe.</summary>
    X86,
}

/// <summary>One run of the install script.</summary>
internal sealed record ScriptRun(int ExitCode, bool ProcessTreeExited, string StdOut, string StdErr, string Log)
{
    public string Describe() =>
        $"exit code: {ExitCode}\n--- stdout:\n{StdOut}\n--- stderr:\n{StdErr}\n--- intune-install.log:\n{Log}";
}

/// <summary>A start of the stand-in agent, as it wrote it down.</summary>
internal sealed record AgentStart(int Pid, int SessionId, string WindowStation, string Desktop, string[] Args);

/// <summary>
/// An unpacked Intune package and a stand-in profile, in a temp folder that is
/// removed on dispose, plus a throwaway HKCU key standing in for
/// <c>HKCU\...\Run</c>:
/// <code>
///   &lt;root&gt;\package\                     Install-AnchorAgent.ps1 + the stand-in Setup.exe
///   &lt;root&gt;\LocalAppData\Anchor.Agent\     the install folder Setup.exe "installs" into
///   &lt;root&gt;\LocalAppData\Anchor\FocusAgent\logs\   intune-install.log, intune-setup.log
/// </code>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class IntuneSandbox : IDisposable
{
    private const string RunKeyRoot = @"Software\AnchorTests";
    private const string SetupFileName = "Anchor.Agent-win-Setup.exe";
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan TreeTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Root { get; }
    public string PackageDir => Path.Combine(Root, "package");
    public string ScriptPath => Path.Combine(PackageDir, "Install-AnchorAgent.ps1");
    public string SetupExe => Path.Combine(PackageDir, SetupFileName);
    public string InstallRoot => Path.Combine(Root, "LocalAppData", "Anchor.Agent");
    public string AgentExe => Path.Combine(InstallRoot, "current", "FocusAgent.App.exe");
    public string LogPath => Path.Combine(Root, "LocalAppData", "Anchor", "FocusAgent", "logs", "intune-install.log");
    public string SetupLogPath => Path.Combine(Path.GetDirectoryName(LogPath)!, "intune-setup.log");

    private readonly string _keyToDelete;
    private string RunKey => $@"{_keyToDelete}\Run";

    private IntuneSandbox(string root, string keyToDelete)
    {
        Root = root;
        _keyToDelete = keyToDelete;
    }

    public static IntuneSandbox Create()
    {
        var script = Path.Combine(TestConfig.RepoRoot, "agent", "scripts", "release-assets", "Install-AnchorAgent.ps1");
        Assert.True(File.Exists(script), $"Missing script at {script}.");
        var fakeDir = FakeSetupDir();
        if (!File.Exists(Path.Combine(fakeDir, SetupFileName)))
            throw new FileNotFoundException(
                $"The stand-in Setup.exe was not found in {fakeDir}; it is built with the integration tests " +
                "(ProjectReference to FocusAgent.FakeSetup).", Path.Combine(fakeDir, SetupFileName));

        var id = Guid.NewGuid().ToString("N");
        var sandbox = new IntuneSandbox(Path.Combine(Path.GetTempPath(), $"anchor-intune-{id}"), $@"{RunKeyRoot}\{id}");
        Directory.CreateDirectory(sandbox.PackageDir);
        File.Copy(script, sandbox.ScriptPath);
        foreach (var file in FakeSetupFiles(fakeDir))
            File.Copy(file, Path.Combine(sandbox.PackageDir, Path.GetFileName(file)));
        return sandbox;
    }

    /// <summary>
    /// Put the stand-in agent in place as an earlier install would have, and its Run
    /// value with it unless <paramref name="withRunValue"/> is false.
    /// </summary>
    public void InstallAgent(bool withRunValue)
    {
        var current = Path.GetDirectoryName(AgentExe)!;
        Directory.CreateDirectory(current);
        foreach (var file in FakeSetupFiles(FakeSetupDir()))
        {
            var name = Path.GetFileName(file);
            File.Copy(file, Path.Combine(current, name == SetupFileName ? Path.GetFileName(AgentExe) : name));
        }
        if (withRunValue)
            WriteRunValue(StartupRunKey.CommandFor(AgentExe));
    }

    /// <summary>Write the agent's Run value, as its install hook does (#225).</summary>
    public void WriteRunValue(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key.SetValue(StartupRunKey.ValueName, command, RegistryValueKind.String);
    }

    /// <summary>Make the installed stand-in agent exit as soon as it starts.</summary>
    public void MakeAgentExitAtStart() =>
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(AgentExe)!, "exit-at-start"), "");

    /// <summary>Start the installed stand-in agent as a sign-in would, and return its PID.</summary>
    public async Task<int> StartAgentAsync()
    {
        using var process = Process.Start(new ProcessStartInfo(AgentExe) { UseShellExecute = false })
            ?? throw new InvalidOperationException("Failed to start the stand-in agent.");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!ReadAgentStarts().Any(s => s.Pid == process.Id))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The stand-in agent didn't write down its start.");
            await Task.Delay(100);
        }
        return process.Id;
    }

    /// <summary>
    /// Run the script as Intune's install command would, inside a job object, and
    /// wait for it to exit and then for its whole process tree to be gone and its
    /// output to close. <paramref name="setupEnvironment"/> configures the stand-in
    /// Setup.exe, which gets the script's environment.
    /// </summary>
    public async Task<ScriptRun> RunScriptAsync(
        PowerShellHost host, IReadOnlyDictionary<string, string>? setupEnvironment = null)
    {
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            host == PowerShellHost.X86 ? "SysWOW64" : "System32",
            "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.True(File.Exists(powershell), $"No {host} Windows PowerShell at {powershell}.");

        var psi = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // Not the package folder: the script finds Setup.exe beside itself.
            WorkingDirectory = Root,
        };
        foreach (var arg in new[]
                 {
                     "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", ScriptPath,
                     "-InstallRoot", InstallRoot, "-LogPath", LogPath, "-RunKeyPath", $@"HKCU:\{RunKey}",
                 })
        {
            psi.ArgumentList.Add(arg);
        }
        psi.Environment["ANCHOR_FAKE_SETUP_INSTALL_ROOT"] = InstallRoot;
        psi.Environment["ANCHOR_FAKE_SETUP_RUN_KEY"] = RunKey;
        foreach (var (name, value) in setupEnvironment ?? new Dictionary<string, string>())
            psi.Environment[name] = value;

        using var job = JobObject.Create();
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {powershell}.");
        // PowerShell takes far longer to start up than this takes, so the script's
        // own children all start inside the job.
        job.Assign(process);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using (var cts = new CancellationTokenSource(ScriptTimeout))
        {
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                throw new TimeoutException(
                    $"The install script didn't exit within {ScriptTimeout.TotalSeconds:N0} s.\n--- log:\n{ReadLog()}");
            }
        }

        var treeExited = await job.WaitUntilEmptyAsync(TreeTimeout);
        // Nothing left holding the script's output pipes open either.
        var output = Task.WhenAll(stdout, stderr);
        await Task.WhenAny(output, Task.Delay(TreeTimeout));
        var outputClosed = output.IsCompleted;
        return new ScriptRun(
            process.ExitCode,
            treeExited && outputClosed,
            stdout.IsCompleted ? stdout.Result : "<still open>",
            stderr.IsCompleted ? stderr.Result : "<still open>",
            ReadLog());
    }

    /// <summary>Every start of the stand-in Setup.exe so far, as its argument list.</summary>
    public IReadOnlyList<string[]> ReadSetupInvocations() =>
        ReadLines(Path.Combine(PackageDir, "setup-invocations.jsonl"))
            .Select(line => JsonSerializer.Deserialize<string[]>(line) ?? [])
            .ToList();

    /// <summary>Every start of the installed stand-in agent so far.</summary>
    public IReadOnlyList<AgentStart> ReadAgentStarts() =>
        ReadLines(Path.Combine(Path.GetDirectoryName(AgentExe)!, "agent-starts.jsonl"))
            .Select(line => JsonSerializer.Deserialize<AgentStart>(line, Json)!)
            .ToList();

    /// <summary>The PIDs of the processes running from the installed agent's exe.</summary>
    public IReadOnlyList<int> RunningAgentPids() =>
        ProcessesUnder(AgentExe, exact: true);

    public string ReadLog() => File.Exists(LogPath) ? File.ReadAllText(LogPath) : "<no log>";

    private static IEnumerable<string> ReadLines(string path)
    {
        if (!File.Exists(path)) return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }

    // The PIDs of the processes whose exe is (or is under) the given path. The
    // agent the script starts runs outside this test's process tree.
    private static List<int> ProcessesUnder(string path, bool exact)
    {
        var pids = new List<int>();
        foreach (var process in Process.GetProcessesByName("FocusAgent.App"))
        {
            using (process)
            {
                string? exe;
                try { exe = process.MainModule?.FileName; }
                catch (Win32Exception) { continue; }
                catch (InvalidOperationException) { continue; }
                if (exe is null) continue;
                var match = exact
                    ? string.Equals(exe, path, StringComparison.OrdinalIgnoreCase)
                    : exe.StartsWith(path, StringComparison.OrdinalIgnoreCase);
                if (match) pids.Add(process.Id);
            }
        }
        return pids;
    }

    // FocusAgent.FakeSetup's output, built in the same configuration as this suite.
    private static string FakeSetupDir()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('\\', '/')).Parent!.Name;
        return Path.Combine(
            TestConfig.RepoRoot, "agent", "tests", "FocusAgent.FakeSetup", "bin", configuration, "net10.0-windows");
    }

    private static IEnumerable<string> FakeSetupFiles(string dir) =>
        Directory.EnumerateFiles(dir, Path.GetFileNameWithoutExtension(SetupFileName) + ".*");

    public void Dispose()
    {
        foreach (var pid in ProcessesUnder(Root + Path.DirectorySeparatorChar, exact: false))
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill();
                process.WaitForExit(5000);
            }
            catch { /* already gone */ }
        }
        try { Registry.CurrentUser.DeleteSubKeyTree(_keyToDelete, throwOnMissingSubKey: false); }
        catch { /* best-effort cleanup */ }
        for (var attempt = 0; attempt < 20 && Directory.Exists(Root); attempt++)
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { Thread.Sleep(250); }
            catch (UnauthorizedAccessException) { Thread.Sleep(250); }
        }
    }
}

/// <summary>
/// A job object the spec runs the script in, standing in for an installer host
/// that waits for every process the install command leaves behind.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class JobObject : IDisposable
{
    private const int JobObjectBasicAccountingInformation = 1;
    private readonly SafeFileHandle _handle;

    private JobObject(SafeFileHandle handle) => _handle = handle;

    public static JobObject Create()
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed.");
        return new JobObject(handle);
    }

    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.Handle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed.");
    }

    /// <summary>Wait until no process is left in the job; false when the timeout passes first.</summary>
    public async Task<bool> WaitUntilEmptyAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (!QueryInformationJobObject(_handle, JobObjectBasicAccountingInformation, out var info,
                    Marshal.SizeOf<BasicAccountingInformation>(), IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "QueryInformationJobObject failed.");
            if (info.ActiveProcesses == 0) return true;
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(100);
        }
    }

    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        SafeFileHandle job, int infoClass, out BasicAccountingInformation info, int length, IntPtr returnLength);
}

/// <summary>The window station and desktop of this process, to compare the agent's with.</summary>
[SupportedOSPlatform("windows")]
internal static class DesktopNames
{
    private const int UoiName = 2;

    public static string WindowStation() => ObjectName(GetProcessWindowStation());

    public static string Desktop() => ObjectName(GetThreadDesktop(GetCurrentThreadId()));

    private static string ObjectName(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return "";
        var buffer = new char[256];
        return GetUserObjectInformation(handle, UoiName, buffer, (uint)(buffer.Length * sizeof(char)), out _)
            ? new string(buffer, 0, Math.Max(0, Array.IndexOf(buffer, '\0')))
            : "";
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetProcessWindowStation();

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint threadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(
        IntPtr handle, int index, [Out] char[] info, uint length, out uint lengthNeeded);
}
