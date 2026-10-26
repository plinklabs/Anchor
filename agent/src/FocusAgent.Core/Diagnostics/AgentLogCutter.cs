using System.Globalization;
using System.Text.RegularExpressions;

namespace FocusAgent.Core.Diagnostics;

/// <summary>
/// An agent process start the agent logged (#446): its process id and when the
/// process started. The updater log's lines carry only a time of day; these
/// starts are what dates them (see <see cref="UpdaterLogCutter"/>).
/// </summary>
public sealed record AgentProcessStart(int ProcessId, DateTimeOffset StartedAt);

/// <summary>
/// Cuts the lines inside a session window out of the agent's own logs (#446),
/// pure string work so it is unit-tested without files. Two formats:
/// <list type="bullet">
///   <item>the daily Serilog files, each entry starting with a local timestamp
///   and offset (<c>2026-10-21 17:08:19.041 +02:00 [INF] ...</c>); lines that
///   don't start with one (a stack trace) belong to the entry above them;</item>
///   <item>the crash log (<c>startup-error.log</c>), each entry a round-trip
///   timestamp on a line of its own followed by the exception text.</item>
/// </list>
/// An entry is kept whole when its timestamp lies inside the window and left
/// out whole otherwise, so nothing from outside the window goes along with it.
/// Lines before a file's first timestamp belong to an entry of unknown time and
/// are left out.
/// </summary>
public static class AgentLogCutter
{
    /// <summary>The Serilog output template's timestamp, <c>{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}</c>.</summary>
    private const string AgentTimestampFormat = "yyyy-MM-dd HH:mm:ss.fff zzz";

    private static readonly Regex AgentTimestamp = new(
        @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}(?= |$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CrashTimestamp = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // App.OnLaunched's startup line, and only that one (not another line that
    // happens to quote these words, a window title say): "<timestamp> [INF]
    // FocusAgent.App.App: FocusAgent 0.9.3 starting ..., process 1234 started at
    // 2026-10-21T07:58:02.1234567+02:00".
    private static readonly Regex ProcessStartLine = new(
        @"^\S+ \S+ \S+ \[INF\] FocusAgent\.App\.App: FocusAgent \S+ starting\b.*, process (?<pid>\d+) started at (?<at>\d{4}-\d{2}-\d{2}T\S+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The agent log's entries inside <paramref name="window"/>, each with its continuation lines.</summary>
    public static IEnumerable<string> CutAgentLog(IEnumerable<string> lines, LogWindow window) =>
        Cut(Entries(lines, TryParseAgentTimestamp), window);

    /// <summary>The crash log's entries inside <paramref name="window"/>, each with its exception text.</summary>
    public static IEnumerable<string> CutCrashLog(IEnumerable<string> lines, LogWindow window) =>
        Cut(Entries(lines, TryParseCrashTimestamp), window);

    /// <summary>
    /// The agent process starts logged in <paramref name="lines"/> (the agent's
    /// own log): read locally to date the updater log, never uploaded for it.
    /// </summary>
    public static IEnumerable<AgentProcessStart> FindProcessStarts(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (!TryParseAgentTimestamp(line, out _)) continue;
            var match = ProcessStartLine.Match(line);
            if (match.Success
                && int.TryParse(match.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                && DateTimeOffset.TryParse(match.Groups["at"].Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var startedAt))
            {
                yield return new AgentProcessStart(pid, startedAt);
            }
        }
    }

    public static bool TryParseAgentTimestamp(string line, out DateTimeOffset at)
    {
        at = default;
        var match = AgentTimestamp.Match(line);
        return match.Success && DateTimeOffset.TryParseExact(
            match.Value, AgentTimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out at);
    }

    private static bool TryParseCrashTimestamp(string line, out DateTimeOffset at)
    {
        at = default;
        return CrashTimestamp.IsMatch(line) && DateTimeOffset.TryParse(
            line.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out at);
    }

    private delegate bool TimestampParser(string line, out DateTimeOffset at);

    private static IEnumerable<string> Cut(IEnumerable<(DateTimeOffset At, string Text)> entries, LogWindow window) =>
        entries.Where(e => window.Contains(e.At)).Select(e => e.Text);

    private static IEnumerable<(DateTimeOffset At, string Text)> Entries(IEnumerable<string> lines, TimestampParser parse)
    {
        DateTimeOffset? at = null;
        var text = new List<string>();
        foreach (var line in lines)
        {
            if (parse(line, out var next))
            {
                if (at is { } done) yield return (done, string.Join('\n', TrimTrailingBlankLines(text)));
                at = next;
                text.Clear();
                text.Add(line);
            }
            else if (at is not null)
            {
                text.Add(line);
            }
            // else: before the first timestamp, of unknown time: left out.
        }
        if (at is { } last) yield return (last, string.Join('\n', TrimTrailingBlankLines(text)));
    }

    private static IEnumerable<string> TrimTrailingBlankLines(List<string> lines)
    {
        var count = lines.Count;
        while (count > 1 && string.IsNullOrWhiteSpace(lines[count - 1])) count--;
        return lines.Take(count);
    }
}
