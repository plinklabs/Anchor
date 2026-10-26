using System.Diagnostics;
using FocusAgent.Core.Logging;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// #463: an agent the suite launches logs to the run's temporary folder
/// (<see cref="AgentTestLogs"/>), never into <c>%LOCALAPPDATA%\Anchor\FocusAgent\logs</c>,
/// where an agent installed on the same box writes its own logs and, since #446,
/// uploads them from session windows. The spec launches the agent the way nearly
/// every spec does, through <see cref="AgentProcess.Launch"/> with no log folder of
/// its own, and finds the agent's start line, which names its process and start
/// time, in the run's folder and not in the machine's. It only reads the machine's
/// log.
/// </summary>
[Collection(AgentE2ECollection.Name)]
public sealed class AgentLogFolderTests
{
    private readonly BackendFixture _backend;
    public AgentLogFolderTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task An_agent_the_harness_launches_logs_to_the_runs_temporary_folder_and_not_to_the_machines_agent_log()
    {
        await using var agent = AgentProcess.Launch(_backend.Url, TestConfig.StudentOid);

        // The start line is written right after the host starts, before /status listens.
        var snap = await agent.WaitForAsync(s => !string.IsNullOrEmpty(s.AgentVersion), TimeSpan.FromSeconds(20));
        Assert.NotNull(snap);

        string startLine;
        using (var process = Process.GetProcessById(agent.Pid))
            startLine = $"process {agent.Pid} started at {new DateTimeOffset(process.StartTime):o}";

        var runFolder = AgentTestLogs.Folder;
        Assert.True(
            AgentLogLines(runFolder).Any(l => l.Contains(startLine)),
            $"The agent's start line ('{startLine}') is not in the run's log folder {runFolder}.");

        var machineFolder = AgentLogPaths.LocalAppDataLogDirectory();
        Assert.DoesNotContain(AgentLogLines(machineFolder), l => l.Contains(startLine));
    }

    /// <summary>The lines of the folder's daily agent logs, read without locking them.</summary>
    private static List<string> AgentLogLines(string folder)
    {
        var lines = new List<string>();
        if (!Directory.Exists(folder)) return lines;
        foreach (var path in Directory.GetFiles(folder, "focusagent-*.log"))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line) lines.Add(line);
        }
        return lines;
    }
}
