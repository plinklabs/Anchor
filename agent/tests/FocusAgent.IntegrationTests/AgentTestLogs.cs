using System.Diagnostics;
using System.Runtime.CompilerServices;
using FocusAgent.Core.Logging;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// The folder every agent the e2e launches logs to (#463): a temporary one per run.
/// Left to its default, a launched agent writes into
/// <c>%LOCALAPPDATA%\Anchor\FocusAgent\logs</c>, where an agent installed on the
/// same box (a developer's laptop, a runner) writes its own logs, and uploads them
/// from session windows when the backend asks (#446).
///
/// <para>
/// The module initializer sets <c>Diagnostics__LogDirectory</c> and
/// <c>Diagnostics__UpdaterLogPath</c> on the test process before any spec runs, so
/// every process the suite starts inherits them: <see cref="AgentProcess.Launch"/>,
/// <see cref="AgentSelfTestProcess.Launch"/>, the specs' own launches
/// (<c>StartupCrashUpdateTests</c>, <c>I18nTests</c>, ...) and any launch a later
/// spec adds. The agent moves its logs on any dev launch (<c>Program.LogDirectory</c>):
/// its daily logs, the self-tests' logs and the crash log (<c>startup-error.log</c>)
/// land here, and its log upload reads only from here. A spec that needs a folder of
/// its own passes it per launch, which wins over the inherited value
/// (<c>LogUploadTests</c>, through <c>extraEnvironment</c>).
/// </para>
///
/// <para>
/// The run deletes its folder when the test host exits. The host is often stopped
/// while Windows still holds the log of the agent the last spec killed, so the next
/// run also deletes the folders of runs whose test host is gone. To keep the logs of
/// a local run, set <c>Diagnostics__LogDirectory</c> before <c>dotnet test</c>: the
/// suite then logs to that folder and leaves it in place.
/// </para>
/// </summary>
internal static class AgentTestLogs
{
    private const string UpdaterLogPathEnvVar = "Diagnostics__UpdaterLogPath";

    // Followed by the test host's process id, so a later run can tell a folder whose
    // run has ended from one a run alongside it (another worktree) still uses.
    private const string FolderPrefix = "anchor-agent-e2e-run-logs-";

    private static readonly string? Preset =
        Environment.GetEnvironmentVariable(AgentLogPaths.LogDirectoryEnvVar) is { Length: > 0 } folder
            ? folder
            : null;

    /// <summary>The folder this run's agents log to.</summary>
    public static string Folder { get; } =
        Preset ?? Path.Combine(Path.GetTempPath(), $"{FolderPrefix}{Environment.ProcessId}-{Guid.NewGuid():N}");

    [ModuleInitializer]
    internal static void PointEveryLaunchedAgentAtTheRunsFolder()
    {
        Environment.SetEnvironmentVariable(AgentLogPaths.LogDirectoryEnvVar, Folder);
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(UpdaterLogPathEnvVar)))
        {
            // The log upload reads Velopack's log from here instead of the one an
            // installed agent's updates write under %LOCALAPPDATA%\velopack.
            Environment.SetEnvironmentVariable(UpdaterLogPathEnvVar, Path.Combine(Folder, "velopack_Anchor.Agent.log"));
        }
        if (Preset is not null) return;

        DeleteFoldersOfEndedRuns();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(Folder);
    }

    private static void DeleteFoldersOfEndedRuns()
    {
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(Path.GetTempPath(), FolderPrefix + "*"))
            {
                var rest = Path.GetFileName(folder)[FolderPrefix.Length..];
                var dash = rest.IndexOf('-');
                if (dash > 0 && int.TryParse(rest[..dash], out var testHost) && !IsRunning(testHost))
                    TryDelete(folder);
            }
        }
        catch
        {
            // Best-effort: leftovers only take a little room under the temp dir.
        }
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch
        {
            return true; // can't tell (another user's process): leave the folder
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch
        {
            // Best-effort; see DeleteFoldersOfEndedRuns.
        }
    }
}
