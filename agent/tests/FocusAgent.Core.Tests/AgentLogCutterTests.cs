using FocusAgent.Core.Diagnostics;

namespace FocusAgent.Core.Tests;

/// <summary>
/// #446: the log upload cuts the agent's own log and its crash log to a session
/// window, entry by entry: an entry inside goes whole (its stack trace too), an
/// entry outside stays whole on the laptop, and lines of unknown time stay too.
/// </summary>
public class AgentLogCutterTests
{
    private static readonly LogWindow Window = new(
        Guid.NewGuid(),
        new DateTimeOffset(2026, 10, 21, 7, 30, 0, TimeSpan.FromHours(2)),
        new DateTimeOffset(2026, 10, 21, 9, 10, 0, TimeSpan.FromHours(2)));

    [Fact]
    public void Keeps_the_entries_inside_the_window_with_their_continuation_lines_and_nothing_else()
    {
        string[] lines =
        [
            "System.Exception: a stack trace whose entry is in an earlier file",
            "2026-10-21 07:29:59.999 +02:00 [INF] Before.Window: started at home",
            "   at Before.Window.Frame()",
            "2026-10-21 07:30:00.000 +02:00 [INF] FocusAgent.App.App: on the window's first millisecond",
            "2026-10-21 08:15:42.250 +02:00 [ERR] Inside.Window: failed",
            "System.InvalidOperationException: boom",
            "   at Inside.Window.Frame()",
            "2026-10-21 09:10:00.000 +02:00 [DBG] Inside.Window: on the window's last millisecond",
            "2026-10-21 09:10:00.001 +02:00 [INF] After.Window: back home",
            "   at After.Window.Frame()",
        ];

        var kept = AgentLogCutter.CutAgentLog(lines, Window).ToList();

        Assert.Equal(
            [
                "2026-10-21 07:30:00.000 +02:00 [INF] FocusAgent.App.App: on the window's first millisecond",
                "2026-10-21 08:15:42.250 +02:00 [ERR] Inside.Window: failed\n"
                + "System.InvalidOperationException: boom\n"
                + "   at Inside.Window.Frame()",
                "2026-10-21 09:10:00.000 +02:00 [DBG] Inside.Window: on the window's last millisecond",
            ],
            kept);
    }

    [Fact]
    public void Compares_the_moment_whatever_offset_the_line_was_written_with()
    {
        // 06:15 UTC is 08:15 in the window's +02:00; 09:15 +02:00 is past it.
        string[] lines =
        [
            "2026-10-21 06:15:00.000 +00:00 [INF] Written.In.Utc: inside",
            "2026-10-21 08:15:00.000 +01:00 [INF] Written.In.Winter.Time: outside",
        ];

        Assert.Equal(
            ["2026-10-21 06:15:00.000 +00:00 [INF] Written.In.Utc: inside"],
            AgentLogCutter.CutAgentLog(lines, Window));
    }

    [Fact]
    public void Cuts_the_crash_log_by_its_round_trip_timestamps()
    {
        string[] lines =
        [
            "2026-10-21T07:00:00.0000000+02:00",
            "System.Exception: crashed before the window",
            "   at Somewhere()",
            "",
            "2026-10-21T08:00:00.1234567+02:00",
            "System.Exception: crashed in class",
            "   at Somewhere()",
            "",
            "2026-10-21T19:00:00.0000000+02:00",
            "System.Exception: crashed at home",
            "",
        ];

        Assert.Equal(
            ["2026-10-21T08:00:00.1234567+02:00\nSystem.Exception: crashed in class\n   at Somewhere()"],
            AgentLogCutter.CutCrashLog(lines, Window));
    }

    [Fact]
    public void Finds_the_agent_process_starts_and_only_the_real_startup_line()
    {
        string[] lines =
        [
            "2026-10-21 07:58:02.500 +02:00 [INF] FocusAgent.App.App: FocusAgent 0.9.3 starting (unpackaged WinUI 3), process 1234 started at 2026-10-21T07:58:01.8765432+02:00",
            // A window title quoting the same words is no process start.
            "2026-10-21 08:20:00.000 +02:00 [DBG] FocusAgent.Native.ForegroundWatcher: foreground 'process 999 started at 2026-10-21T08:00:00+02:00'",
            "   FocusAgent.App.App: FocusAgent 1.0 starting, process 77 started at 2026-10-21T08:00:00+02:00",
            "2026-10-22 08:01:00.000 +02:00 [INF] FocusAgent.App.App: FocusAgent 0.9.4 starting (unpackaged WinUI 3), process 4321 started at 2026-10-22T08:00:59.0000000+02:00",
        ];

        Assert.Equal(
            [
                new AgentProcessStart(1234, new DateTimeOffset(2026, 10, 21, 7, 58, 1, TimeSpan.FromHours(2)).AddTicks(8765432)),
                new AgentProcessStart(4321, new DateTimeOffset(2026, 10, 22, 8, 0, 59, TimeSpan.FromHours(2))),
            ],
            AgentLogCutter.FindProcessStarts(lines));
    }
}
