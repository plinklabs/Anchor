using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

// The #457 e2e's stand-in for Velopack's Setup.exe and, once "installed", for the
// agent itself (see the .csproj). The role follows from the file name it runs as.
var selfPath = Environment.ProcessPath
    ?? throw new InvalidOperationException("No process path.");
return string.Equals(Path.GetFileNameWithoutExtension(selfPath), "FocusAgent.App", StringComparison.OrdinalIgnoreCase)
    ? FakeAgent.Run(args)
    : FakeSetup.Run(args, selfPath);

/// <summary>
/// The installer role. Configured by the spec through environment variables, which
/// reach it the way the real Setup.exe's environment does: from the script.
/// </summary>
internal static class FakeSetup
{
    // The exit code to end with (default 0).
    private const string ExitCodeVar = "ANCHOR_FAKE_SETUP_EXIT_CODE";
    // The reason written to the --log file when the exit code isn't 0.
    private const string ErrorVar = "ANCHOR_FAKE_SETUP_ERROR";
    // The install folder to "install" into: <root>\current\FocusAgent.App.exe.
    private const string InstallRootVar = "ANCHOR_FAKE_SETUP_INSTALL_ROOT";
    // "1": exit with 0 without installing anything.
    private const string SkipInstallVar = "ANCHOR_FAKE_SETUP_SKIP_INSTALL";
    // An HKCU-relative key to write the agent's Run value into, as its install hook
    // does into HKCU\...\Run (#225). Unset: no Run value.
    private const string RunKeyVar = "ANCHOR_FAKE_SETUP_RUN_KEY";

    // StartupRunKey.ValueName, the value the agent's install hook writes.
    private const string RunValueName = "AnchorFocusAgent";

    public static int Run(string[] args, string selfPath)
    {
        Records.Append("setup-invocations.jsonl", JsonSerializer.Serialize(args));
        var log = ValueAfter(args, "--log");
        WriteLog(log, $"fake Setup started: {string.Join(' ', args)}");

        var exitCode = int.TryParse(Environment.GetEnvironmentVariable(ExitCodeVar), out var code) ? code : 0;
        if (exitCode != 0)
        {
            WriteLog(log, $"[ERROR] {Environment.GetEnvironmentVariable(ErrorVar) ?? "install failed"}");
            return exitCode;
        }
        if (Environment.GetEnvironmentVariable(SkipInstallVar) == "1")
        {
            WriteLog(log, "installed nothing");
            return 0;
        }

        var root = Environment.GetEnvironmentVariable(InstallRootVar);
        if (string.IsNullOrWhiteSpace(root))
        {
            WriteLog(log, $"[ERROR] {InstallRootVar} is not set");
            return 98;
        }

        // The agent: this same program under the agent's file name. A renamed .NET
        // apphost still loads the assembly it was built for, so that goes along too.
        var current = Path.Combine(root, "current");
        Directory.CreateDirectory(current);
        var selfDir = Path.GetDirectoryName(selfPath)!;
        var baseName = Path.GetFileNameWithoutExtension(selfPath);
        foreach (var file in Directory.EnumerateFiles(selfDir, baseName + ".*"))
        {
            var target = string.Equals(file, selfPath, StringComparison.OrdinalIgnoreCase)
                ? "FocusAgent.App.exe"
                : Path.GetFileName(file);
            File.Copy(file, Path.Combine(current, target), overwrite: true);
        }
        var agentExe = Path.Combine(current, "FocusAgent.App.exe");

        var runKey = Environment.GetEnvironmentVariable(RunKeyVar);
        if (!string.IsNullOrWhiteSpace(runKey))
        {
            using var key = Registry.CurrentUser.CreateSubKey(runKey, writable: true);
            key.SetValue(RunValueName, $"\"{agentExe}\"", RegistryValueKind.String);
        }

        WriteLog(log, $"installed {agentExe}");
        return 0;
    }

    private static void WriteLog(string? path, string line)
    {
        if (path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, line + Environment.NewLine);
    }

    private static string? ValueAfter(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == flag) return args[i + 1];
        return null;
    }
}

/// <summary>
/// The agent role: write down how this process was started, then stay up like the
/// agent until the spec stops it. With a file named <c>exit-at-start</c> beside it,
/// it exits right after writing that down instead, like an agent that crashes on start.
/// </summary>
internal static class FakeAgent
{
    // A leaked stand-in (a spec that died before its cleanup) stops on its own.
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(10);

    public static int Run(string[] args)
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        Records.Append("agent-starts.jsonl", JsonSerializer.Serialize(new
        {
            pid = self.Id,
            sessionId = self.SessionId,
            windowStation = Native.WindowStationName(),
            desktop = Native.DesktopName(),
            args,
        }));

        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "exit-at-start")))
            return 3;

        Thread.Sleep(MaxLifetime);
        return 0;
    }
}

internal static class Records
{
    /// <summary>Append one line to <paramref name="fileName"/> beside this exe.</summary>
    public static void Append(string fileName, string line)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
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

internal static class Native
{
    private const int UoiName = 2;

    public static string WindowStationName() => ObjectName(GetProcessWindowStation());

    public static string DesktopName() => ObjectName(GetThreadDesktop(GetCurrentThreadId()));

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
