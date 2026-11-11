using System.Globalization;
using System.Text.RegularExpressions;

namespace FocusAgent.Core.Diagnostics;

/// <summary>
/// Cuts the lines inside a session window out of the Velopack updater's log
/// (#446). Its lines carry a local time of day but no date
/// (<c>[lib-csharp:21152] [10:00:19] [Information] ...</c>), so a line only goes
/// along when it certainly lies inside the window: not one line from outside a
/// window may leave the laptop.
///
/// <para>
/// The file is appended to in order, so every line is no earlier than the line
/// above it and no later than the line below it. Two things pin a line's time:
/// <list type="bullet">
///   <item>the agent's own (dated) log records each agent process start with its
///   process id (<see cref="AgentProcessStart"/>); Velopack's
///   "Initializing ...Locator" line of that process, which it writes within
///   seconds of the start, carries the same process id and a time of day just
///   after the start's, so it falls on the start's date;</item>
///   <item>no line is later than now.</item>
/// </list>
/// From each pinned line a pass down the file gives every later line its
/// earliest possible time (the first moment at or after the line above with its
/// time of day), and a pass up the file from now its latest possible time. A
/// line goes along only when both lie inside the window. A day the laptop spent
/// off between two lines widens the bounds a day apart or more, which no window
/// holds, so such a line is left out rather than guessed. A time of day the
/// clock skipped or repeated (a daylight saving change) leaves the bounds open
/// until the next pin. A contradiction between the bounds (a pin that doesn't
/// fit) leaves out the whole file.
/// </para>
/// </summary>
public static class UpdaterLogCutter
{
    /// <summary>
    /// How long after its process started Velopack's locator line may be
    /// written for it to pin the start's date. It is written within seconds, at
    /// the very top of the agent's Main.
    /// </summary>
    internal static readonly TimeSpan StartTolerance = TimeSpan.FromMinutes(5);

    private static readonly Regex Prefix = new(
        @"^\[(?<source>[^\]:]+):(?<pid>\d+)\] \[(?<h>\d{2}):(?<m>\d{2}):(?<s>\d{2})\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <param name="lines">The log's lines, oldest first (the tail of the file is enough).</param>
    /// <param name="starts">The agent process starts from the agent's own log.</param>
    /// <param name="window">The session window.</param>
    /// <param name="now">Now: no line is later.</param>
    /// <param name="timeZone">The time zone the updater wrote its times of day in (the laptop's).</param>
    public static IReadOnlyList<string> Cut(
        IEnumerable<string> lines,
        IEnumerable<AgentProcessStart> starts,
        LogWindow window,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        var entries = Parse(lines);
        if (entries.Count == 0) return [];

        var pins = Pin(entries, starts, timeZone);

        // Each line's time is its stamp (hh:mm:ss) plus less than a second.
        // The passes bound the stamps.

        // Down the file: the earliest each stamp can be. Open above the first pin.
        var earliest = new DateTimeOffset?[entries.Count];
        DateTimeOffset? floor = null;
        for (var i = 0; i < entries.Count; i++)
        {
            var bound = floor is { } f ? AtOrAfter(f, entries[i].TimeOfDay, timeZone) : null;
            if (pins[i] is { } pin)
            {
                if (bound > pin) return [];
                bound = pin;
            }
            earliest[i] = floor = bound;
        }

        // Up the file from now: the latest each stamp can be.
        var latest = new DateTimeOffset?[entries.Count];
        DateTimeOffset? ceiling = Seconds(now);
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var bound = ceiling is { } c ? AtOrBefore(c, entries[i].TimeOfDay, timeZone) : null;
            if (pins[i] is { } pin)
            {
                if (bound < pin) return [];
                bound = pin;
            }
            if (earliest[i] > bound) return [];
            latest[i] = ceiling = bound;
        }

        var kept = new List<string>();
        for (var i = 0; i < entries.Count; i++)
        {
            if (earliest[i] is { } low && latest[i] is { } high
                && low >= window.From && high.AddSeconds(1) <= window.To)
            {
                kept.Add(entries[i].Text);
            }
        }
        return kept;
    }

    private static DateTimeOffset?[] Pin(
        IReadOnlyList<Entry> entries,
        IEnumerable<AgentProcessStart> starts,
        TimeZoneInfo timeZone)
    {
        var pins = new DateTimeOffset?[entries.Count];
        foreach (var byProcess in starts.GroupBy(s => s.ProcessId))
        {
            // The latest start first, matched to the last fitting locator line;
            // each earlier start only to a line above the one matched before it,
            // so a process id Windows reused never pins one line twice.
            var below = entries.Count;
            foreach (var start in byProcess.OrderByDescending(s => s.StartedAt))
            {
                var startedAt = Seconds(start.StartedAt);
                for (var i = below - 1; i >= 0; i--)
                {
                    var entry = entries[i];
                    if (entry.ProcessId != start.ProcessId || !entry.IsLocatorStart) continue;
                    if (AtOrAfter(startedAt, entry.TimeOfDay, timeZone) is not { } at
                        || at - startedAt > StartTolerance)
                    {
                        continue;
                    }
                    pins[i] = at;
                    below = i;
                    break;
                }
            }
        }
        return pins;
    }

    private static List<Entry> Parse(IEnumerable<string> lines)
    {
        var entries = new List<Entry>();
        foreach (var line in lines)
        {
            var match = Prefix.Match(line);
            if (match.Success
                && int.TryParse(match.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                && TryTimeOfDay(match, out var timeOfDay))
            {
                var isLocatorStart = line.Contains("Initializing", StringComparison.Ordinal)
                                     && line.Contains("Locator", StringComparison.Ordinal);
                entries.Add(new Entry(pid, timeOfDay, line, isLocatorStart));
            }
            else if (entries.Count > 0)
            {
                // A continuation of the line above, timed with it.
                var last = entries[^1];
                entries[^1] = last with { Text = last.Text + "\n" + line };
            }
            // else: above the first timed line, of unknown time: left out.
        }
        return entries;
    }

    private static bool TryTimeOfDay(Match match, out TimeSpan timeOfDay)
    {
        var h = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        var m = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        var s = int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);
        timeOfDay = new TimeSpan(h, m, s);
        return h < 24 && m < 60 && s < 60;
    }

    /// <summary>
    /// The first moment at or after <paramref name="floor"/> whose local time of
    /// day is <paramref name="timeOfDay"/>; null when, on that day, the clock
    /// skipped or repeated that time.
    /// </summary>
    private static DateTimeOffset? AtOrAfter(DateTimeOffset floor, TimeSpan timeOfDay, TimeZoneInfo timeZone)
    {
        var day = TimeZoneInfo.ConvertTime(floor, timeZone).Date;
        for (var i = 0; i < 3; i++, day = day.AddDays(1))
        {
            if (At(day, timeOfDay, timeZone, out var at) is false) return null;
            if (at >= floor) return at;
        }
        return null;
    }

    /// <summary>
    /// The last moment at or before <paramref name="ceiling"/> whose local time
    /// of day is <paramref name="timeOfDay"/>; null when, on that day, the clock
    /// skipped or repeated that time.
    /// </summary>
    private static DateTimeOffset? AtOrBefore(DateTimeOffset ceiling, TimeSpan timeOfDay, TimeZoneInfo timeZone)
    {
        var day = TimeZoneInfo.ConvertTime(ceiling, timeZone).Date;
        for (var i = 0; i < 3; i++, day = day.AddDays(-1))
        {
            if (At(day, timeOfDay, timeZone, out var at) is false) return null;
            if (at <= ceiling) return at;
        }
        return null;
    }

    private static bool At(DateTime day, TimeSpan timeOfDay, TimeZoneInfo timeZone, out DateTimeOffset at)
    {
        var wall = DateTime.SpecifyKind(day + timeOfDay, DateTimeKind.Unspecified);
        at = default;
        if (timeZone.IsInvalidTime(wall) || timeZone.IsAmbiguousTime(wall)) return false;
        at = new DateTimeOffset(wall, timeZone.GetUtcOffset(wall));
        return true;
    }

    private static DateTimeOffset Seconds(DateTimeOffset at) =>
        new(at.Ticks - at.Ticks % TimeSpan.TicksPerSecond, at.Offset);

    private sealed record Entry(int ProcessId, TimeSpan TimeOfDay, string Text, bool IsLocatorStart);
}
