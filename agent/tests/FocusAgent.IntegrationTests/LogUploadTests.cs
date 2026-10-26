using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace FocusAgent.IntegrationTests;

/// <summary>
/// The test phase's log uploads (#446), end to end: the real agent against the
/// real backend. During the test phase each agent uploads its log lines from a
/// window around each session its student was on the roster of — and not one
/// line from outside it, since the laptops belong to minors. The backend decides
/// the windows and is off by default.
///
/// <para>
/// The agent runs on a temporary log folder of its own (Diagnostics__LogDirectory),
/// seeded with lines before, inside and after the session's window in each log
/// it uploads from: its own daily files (an older one too), the crash log and
/// the updater log. Its own lines land there as well.
/// </para>
///
/// <para>
/// With the shared backend's defaults (uploads off) the agent sends nothing,
/// through a session and a restart. Then the backend is switched on, with a
/// five-second window end so the spec needn't wait ten minutes: the running
/// agent reconnects and uploads the window, which the backend then holds with
/// exactly the agent log entries inside it, the crash and updater lines inside
/// it, and nothing from before or after. Downloading it needs an admin, so the
/// spec promotes the seeded teacher at the end and resets the backend's
/// database afterwards.
/// </para>
/// </summary>
[Collection(AgentE2ECollection.Name)]
public sealed class LogUploadTests
{
    private static readonly TimeSpan WindowAfter = TimeSpan.FromSeconds(5);
    private const string AgentTimestampFormat = "yyyy-MM-dd HH:mm:ss.fff zzz";

    private static readonly Regex AgentTimestamp = new(
        @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}(?= |$)", RegexOptions.CultureInvariant);

    private readonly BackendFixture _backend;
    public LogUploadTests(BackendFixture backend) => _backend = backend;

    [Fact]
    public async Task OffTheAgentUploadsNothing_OnItUploadsExactlyTheLinesInsideTheSessionWindow()
    {
        var api = new BackendClient(_backend.Url);
        var logs = Directory.CreateTempSubdirectory("anchor-agent-e2e-logs-").FullName;
        var updaterLog = Path.Combine(logs, "velopack_Anchor.Agent.log");
        var environment = new Dictionary<string, string>
        {
            ["Diagnostics__LogDirectory"] = logs,
            ["Diagnostics__UpdaterLogPath"] = updaterLog,
        };
        AgentProcess? agent = null;
        try
        {
            // --- seed: lines before and inside the coming window --------------
            var seededAt = DateTimeOffset.Now;
            var today = Path.Combine(logs, $"focusagent-{seededAt:yyyyMMdd}.log");
            File.WriteAllLines(Path.Combine(logs, $"focusagent-{seededAt.AddDays(-3):yyyyMMdd}.log"),
            [
                AgentLine(seededAt.AddDays(-3), "Seed.OldFile: SEED-OLD-FILE three days ago"),
            ]);
            File.WriteAllLines(today,
            [
                AgentLine(seededAt.AddHours(-2), "Seed.Before: SEED-BEFORE laptop started at home"),
                "   at Seed.Before.Frame() SEED-BEFORE-TRACE",
                AgentLine(seededAt.AddMinutes(-10), "Seed.Inside: SEED-INSIDE agent started before class"),
                "   at Seed.Inside.Frame() SEED-INSIDE-TRACE",
            ]);
            File.WriteAllLines(Path.Combine(logs, "startup-error.log"),
            [
                CrashLine(seededAt.AddHours(-2)), "System.Exception: SEED-CRASH-BEFORE", "",
                CrashLine(seededAt.AddMinutes(-10)), "System.Exception: SEED-CRASH-INSIDE", "",
            ]);

            // --- uploads off (the shared backend's default): nothing goes -----
            agent = AgentProcess.Launch(_backend.Url, TestConfig.StudentOid, autoJoin: true,
                reconnectMaxBackoff: TimeSpan.FromSeconds(2), extraEnvironment: environment);
            await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));
            var first = await WaitForPassesAsync(agent, 1);
            Assert.Equal(0, first.Uploaded);
            Assert.Null(first.LastError);
            int firstPid;
            DateTimeOffset firstStartedAt;
            using (var process = Process.GetProcessById(agent.Pid))
            {
                firstPid = process.Id;
                firstStartedAt = new DateTimeOffset(process.StartTime);
            }

            var classId = await api.FindClassIdAsync();
            var session = await api.StartSessionAsync(classId);
            var joined = await agent.WaitForAsync(s => s.JoinedSessionId == session.Id, TimeSpan.FromSeconds(8));
            Assert.True(joined?.JoinedSessionId == session.Id, "Agent did not auto-join within 8s.");
            await Task.Delay(TimeSpan.FromSeconds(2));
            await api.EndSessionAsync(session.Id);
            var endedAt = await api.GetSessionEndedAtAsync(session.Id)
                          ?? throw new InvalidOperationException("The session did not end.");
            await agent.WaitForAsync(s => s.JoinedSessionId is null, TimeSpan.FromSeconds(8));

            // Past the window's end (once the backend is switched on below), the
            // laptop is closed: the agent goes away, and lines after the window
            // get written while it is off.
            await WaitUntilAsync(endedAt + WindowAfter + TimeSpan.FromSeconds(3));
            await agent.DisposeAsync();
            agent = null;
            var afterAt = DateTimeOffset.Now;
            await AppendAsync(today,
            [
                AgentLine(afterAt, "Seed.After: SEED-AFTER back home"),
                "   at Seed.After.Frame() SEED-AFTER-TRACE",
            ]);
            File.AppendAllLines(Path.Combine(logs, "startup-error.log"),
                [CrashLine(afterAt), "System.Exception: SEED-CRASH-AFTER", ""]);
            // The updater's lines carry only a time of day. The locator line of the
            // first agent process (a second after its start, as Velopack writes it)
            // dates them; the line before it can't be dated, the last one is after
            // the window.
            File.WriteAllLines(updaterLog,
            [
                $"[update:4242] [{seededAt.AddHours(-2):HH:mm:ss}] [INFO] SEED-UPDATER-BEFORE",
                $"[lib-csharp:{firstPid}] [{firstStartedAt.AddSeconds(1):HH:mm:ss}] [Information] Initializing WindowsVelopackLocator",
                $"[lib-csharp:{firstPid}] [{firstStartedAt.AddSeconds(2):HH:mm:ss}] [Information] SEED-UPDATER-INSIDE",
                $"[update:4243] [{endedAt.ToLocalTime() + WindowAfter + TimeSpan.FromSeconds(2):HH:mm:ss}] [INFO] SEED-UPDATER-AFTER",
            ]);

            // The next start, still off: the agent asks and sends nothing.
            agent = AgentProcess.Launch(_backend.Url, TestConfig.StudentOid, autoJoin: true,
                reconnectMaxBackoff: TimeSpan.FromSeconds(2), extraEnvironment: environment);
            await agent.WaitForConnectedAsync(TimeSpan.FromSeconds(20));
            var off = await WaitForPassesAsync(agent, 1);
            Assert.Equal(0, off.Uploaded);
            Assert.Null(off.LastError);

            // --- switched on, no agent release: the next connect uploads ------
            var switchedOnAt = DateTimeOffset.UtcNow;
            await _backend.StopAsync();
            await _backend.RestartAsync(new Dictionary<string, string>
            {
                ["LogUpload__Enabled"] = "true",
                ["LogUpload__WindowAfter"] = WindowAfter.ToString("c"),
            });
            var on = await agent.WaitForAsync(s => s.LogUpload is { Uploaded: >= 1 }, TimeSpan.FromSeconds(45));
            Assert.True(on?.LogUpload is { Uploaded: 1 },
                $"The agent did not upload the session window after the backend was switched on " +
                $"(logUpload: {on?.LogUpload}).");

            // --- what the backend holds --------------------------------------
            await api.PromoteToAdminAsync();
            var upload = Assert.Single(await api.GetLogUploadsAsync(session.Id));
            Assert.True(upload.IsComplete);
            Assert.Equal(TestConfig.ExpectedAgentVersion, upload.AgentVersion);
            Assert.True(upload.UploadedAt >= switchedOnAt, "An upload was stored while uploads were off.");
            Assert.Equal(endedAt + WindowAfter, upload.WindowTo);
            var window = (From: upload.WindowFrom, To: upload.WindowTo);
            var text = (await api.DownloadLogUploadAsync(session.Id, upload.UserId)).ReplaceLineEndings("\n");
            var sections = Sections(text);

            Assert.Contains($"Session: {session.Id}", sections[""]);
            Assert.Contains($"Agent version: {TestConfig.ExpectedAgentVersion}", sections[""]);

            // Exactly the agent log entries inside the window, as the local files
            // hold them: the seeded one, and the first agent's own (its start, the
            // sign-in, the session) — but not the second agent's.
            var expected = EntriesInside(logs, window.From, window.To);
            Assert.Equal(expected, Entries(sections["Agent log"]));
            var agentLog = string.Join('\n', sections["Agent log"]);
            Assert.Contains("SEED-INSIDE agent started before class\n   at Seed.Inside.Frame() SEED-INSIDE-TRACE", agentLog);
            Assert.Contains($"process {firstPid} started at", agentLog);
            Assert.Contains("Sign-in succeeded", agentLog);
            Assert.DoesNotContain($"process {agent.Pid} started at", agentLog);
            foreach (var outside in new[] { "SEED-OLD-FILE", "SEED-BEFORE", "SEED-AFTER" })
                Assert.DoesNotContain(outside, text);

            Assert.Equal(
                [CrashLine(seededAt.AddMinutes(-10)), "System.Exception: SEED-CRASH-INSIDE"],
                sections["Crash log"]);
            Assert.Equal(
                [
                    $"[lib-csharp:{firstPid}] [{firstStartedAt.AddSeconds(1):HH:mm:ss}] [Information] Initializing WindowsVelopackLocator",
                    $"[lib-csharp:{firstPid}] [{firstStartedAt.AddSeconds(2):HH:mm:ss}] [Information] SEED-UPDATER-INSIDE",
                ],
                sections["Updater log"]);
        }
        finally
        {
            if (agent is not null)
                await agent.DisposeAsync();
            // Back to a fresh seed with the defaults: uploads off, and the seeded
            // teacher a teacher again for the specs that follow.
            await _backend.ResetAsync();
            try { Directory.Delete(logs, recursive: true); } catch { }
        }
    }

    private static string AgentLine(DateTimeOffset at, string message) =>
        $"{at.ToString(AgentTimestampFormat, CultureInfo.InvariantCulture)} [INF] {message}";

    private static string CrashLine(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);

    private static async Task<LogUploadSnapshot> WaitForPassesAsync(AgentProcess agent, int passes)
    {
        var status = await agent.WaitForAsync(s => s.LogUpload?.Passes >= passes, TimeSpan.FromSeconds(20));
        return status?.LogUpload
               ?? throw new TimeoutException($"The agent ran no log-upload pass within 20s of connecting (status: {status}).");
    }

    /// <summary>
    /// Append to a log the agent just stopped writing: the killed process (or a
    /// virus scanner looking at the file) can hold it for a moment longer.
    /// </summary>
    private static async Task AppendAsync(string path, string[] lines)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await File.AppendAllLinesAsync(path, lines);
                return;
            }
            catch (IOException) when (attempt < 50)
            {
                await Task.Delay(200);
            }
        }
    }

    private static async Task WaitUntilAsync(DateTimeOffset at)
    {
        var wait = at - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait);
    }

    /// <summary>The document's sections by title ("" for the header), each as its lines.</summary>
    private static Dictionary<string, List<string>> Sections(string text)
    {
        var sections = new Dictionary<string, List<string>> { [""] = new() };
        var current = sections[""];
        foreach (var line in text.Split('\n'))
        {
            var title = Regex.Match(line, "^== (.+) ==$");
            if (title.Success)
                sections[title.Groups[1].Value] = current = new List<string>();
            else if (line.Length > 0 || current.Count > 0)
                current.Add(line);
        }
        foreach (var lines in sections.Values)
        {
            while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        }
        return sections;
    }

    /// <summary>The agent log entries (a timestamped line and the lines under it) of the folder's daily files inside the window.</summary>
    private static List<string> EntriesInside(string folder, DateTimeOffset from, DateTimeOffset to)
    {
        var entries = new List<string>();
        foreach (var path in Directory.GetFiles(folder, "focusagent-*.log").Order(StringComparer.OrdinalIgnoreCase))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line) lines.Add(line);
            entries.AddRange(Entries(lines).Where(e =>
            {
                var at = DateTimeOffset.ParseExact(
                    AgentTimestamp.Match(e).Value, AgentTimestampFormat, CultureInfo.InvariantCulture);
                return at >= from && at <= to;
            }));
        }
        return entries;
    }

    private static List<string> Entries(IEnumerable<string> lines)
    {
        var entries = new List<string>();
        foreach (var line in lines)
        {
            if (AgentTimestamp.IsMatch(line))
                entries.Add(line);
            else if (entries.Count > 0)
                entries[^1] += "\n" + line;
        }
        return entries.Select(e => e.TrimEnd('\n')).ToList();
    }
}
