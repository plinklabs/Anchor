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
/// through two lessons and a restart. Then the backend is switched on, with a
/// five-second window end so the spec needn't wait ten minutes: the running
/// agent reconnects and uploads every window the backend lists — both lessons',
/// and in the full suite those of the sessions earlier specs left for the same
/// seeded student — and the backend then holds, for each lesson, exactly the
/// agent log entries inside its window, the crash and updater lines inside it,
/// and nothing from before or after. Downloading needs an admin, so the spec
/// promotes the seeded teacher at the end and resets the backend's database
/// afterwards.
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

            // Two lessons of this student, so the agent always has more than one
            // window to upload (in the full suite the specs before this one leave
            // many more of the same student's sessions): each upload has to be
            // cut to its own window. The second starts after the first's window
            // has closed, so its lines fall outside that window.
            var classId = await api.FindClassIdAsync();
            var earlier = await RunLessonAsync(api, agent, classId);
            await WaitUntilAsync(earlier.EndedAt + WindowAfter + TimeSpan.FromSeconds(1));
            var lesson = await RunLessonAsync(api, agent, classId);
            var endedAt = lesson.EndedAt;

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
            // One pass uploads every window the backend lists: this spec's two
            // lessons, and whatever sessions of the same student earlier specs left.
            var on = await agent.WaitForAsync(s => s.LogUpload?.Passes > off.Passes, TimeSpan.FromSeconds(45));
            Assert.True(on?.LogUpload is { LastError: null, Uploaded: >= 2 },
                $"The agent's pass after the backend was switched on did not upload both lessons' windows " +
                $"without an error (logUpload: {on?.LogUpload?.ToString() ?? "<no pass>"}).");

            // --- what the backend holds: each window, exactly -----------------
            await api.PromoteToAdminAsync();
            var earlierLog = await AssertUploadAsync(earlier);
            var agentLog = await AssertUploadAsync(lesson);

            // The first agent's own lines (its start, the sign-in, the lessons) and
            // the seeded one inside the window, but not the second agent's.
            Assert.Contains("SEED-INSIDE agent started before class\n   at Seed.Inside.Frame() SEED-INSIDE-TRACE", agentLog);
            Assert.Contains($"process {firstPid} started at", agentLog);
            Assert.Contains("Sign-in succeeded", agentLog);
            Assert.DoesNotContain($"process {agent.Pid} started at", agentLog);
            // Each upload cut to its own window: the later lesson's window opens
            // 30 minutes before it, over the earlier lesson; the earlier lesson's
            // window closed before the later one started.
            Assert.Contains(earlier.Id.ToString(), agentLog);
            Assert.DoesNotContain(lesson.Id.ToString(), earlierLog);

            // One lesson's upload: complete, sent after the switch, and holding
            // exactly the agent log entries the local files hold inside its window,
            // the crash and updater lines inside it, and nothing from outside it.
            // Returns its agent log section.
            async Task<string> AssertUploadAsync(Lesson of)
            {
                var upload = Assert.Single(await api.GetLogUploadsAsync(of.Id));
                Assert.True(upload.IsComplete);
                Assert.Equal(TestConfig.ExpectedAgentVersion, upload.AgentVersion);
                Assert.True(upload.UploadedAt >= switchedOnAt, "An upload was stored while uploads were off.");
                Assert.Equal(of.EndedAt + WindowAfter, upload.WindowTo);
                var text = (await api.DownloadLogUploadAsync(of.Id, upload.UserId)).ReplaceLineEndings("\n");
                var sections = Sections(text);

                Assert.Contains($"Session: {of.Id}", sections[""]);
                Assert.Contains($"Agent version: {TestConfig.ExpectedAgentVersion}", sections[""]);
                Assert.Equal(EntriesInside(logs, upload.WindowFrom, upload.WindowTo), Entries(sections["Agent log"]));
                foreach (var outside in new[]
                         {
                             "SEED-OLD-FILE", "SEED-BEFORE", "SEED-AFTER", "SEED-CRASH-BEFORE", "SEED-CRASH-AFTER",
                             "SEED-UPDATER-BEFORE", "SEED-UPDATER-AFTER",
                         })
                {
                    Assert.DoesNotContain(outside, text);
                }
                Assert.Equal(
                    [CrashLine(seededAt.AddMinutes(-10)), "System.Exception: SEED-CRASH-INSIDE"],
                    sections["Crash log"]);
                Assert.Equal(
                    [
                        $"[lib-csharp:{firstPid}] [{firstStartedAt.AddSeconds(1):HH:mm:ss}] [Information] Initializing WindowsVelopackLocator",
                        $"[lib-csharp:{firstPid}] [{firstStartedAt.AddSeconds(2):HH:mm:ss}] [Information] SEED-UPDATER-INSIDE",
                    ],
                    sections["Updater log"]);
                return string.Join('\n', sections["Agent log"]);
            }
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

    private sealed record Lesson(Guid Id, DateTimeOffset EndedAt);

    /// <summary>A short lesson the agent joins: started, joined, ended, left.</summary>
    private static async Task<Lesson> RunLessonAsync(BackendClient api, AgentProcess agent, Guid classId)
    {
        var session = await api.StartSessionAsync(classId);
        var joined = await agent.WaitForAsync(s => s.JoinedSessionId == session.Id, TimeSpan.FromSeconds(8));
        Assert.True(joined?.JoinedSessionId == session.Id, "Agent did not auto-join within 8s.");
        await Task.Delay(TimeSpan.FromSeconds(2));
        await api.EndSessionAsync(session.Id);
        var endedAt = await api.GetSessionEndedAtAsync(session.Id)
                      ?? throw new InvalidOperationException("The session did not end.");
        await agent.WaitForAsync(s => s.JoinedSessionId is null, TimeSpan.FromSeconds(8));
        return new Lesson(session.Id, endedAt);
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
