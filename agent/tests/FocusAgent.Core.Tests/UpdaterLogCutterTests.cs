using FocusAgent.Core.Diagnostics;

namespace FocusAgent.Core.Tests;

/// <summary>
/// #446: the updater log's lines carry only a time of day, so a line goes along
/// with a session window only when its date is certain and it certainly lies
/// inside the window. Every case where a line from another day could pass for
/// one inside the window must leave it on the laptop.
/// </summary>
public class UpdaterLogCutterTests
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(2);
    private static readonly TimeZoneInfo Zone =
        TimeZoneInfo.CreateCustomTimeZone("Anchor test +02", Offset, "Anchor test +02", "Anchor test +02");

    // Monday 2026-10-19, a lesson from 10:00 to 11:00: its window is 09:30-11:10.
    private static readonly LogWindow Monday = new(Guid.NewGuid(), At(19, 9, 30), At(19, 11, 10));

    private static DateTimeOffset At(int day, int hour, int minute, int second = 0) =>
        new(2026, 10, day, hour, minute, second, Offset);

    private static string Agent(int pid, string time, string text) => $"[lib-csharp:{pid}] [{time}] [Information] {text}";

    private static string Updater(int pid, string time, string text) => $"[update:{pid}] [{time}] [INFO] {text}";

    private static string Locator(int pid, string time) => Agent(pid, time, "Initializing WindowsVelopackLocator");

    private static IReadOnlyList<string> Cut(
        IEnumerable<string> lines, IEnumerable<AgentProcessStart> starts, DateTimeOffset now, LogWindow? window = null) =>
        UpdaterLogCutter.Cut(lines, starts, window ?? Monday, now, Zone);

    [Fact]
    public void Keeps_the_lines_a_process_start_dates_inside_the_window_and_nothing_around_them()
    {
        // The agent started at 09:45 (its own log says so, with its process id);
        // Velopack's locator line of that process pins the date.
        string[] lines =
        [
            Updater(500, "21:30:00", "before the first pin: unknown day, stays"),
            Locator(1234, "09:45:01"),
            Agent(1234, "09:45:02", "Retrieving latest release feed."),
            "  a continuation of the line above",
            Updater(600, "10:30:00", "applied an update in class"),
            Updater(600, "11:20:00", "after the window"),
        ];

        var kept = Cut(lines, [new AgentProcessStart(1234, At(19, 9, 45))], now: At(19, 12, 0));

        Assert.Equal(
            [
                Locator(1234, "09:45:01"),
                Agent(1234, "09:45:02", "Retrieving latest release feed.") + "\n  a continuation of the line above",
                Updater(600, "10:30:00", "applied an update in class"),
            ],
            kept);
    }

    [Fact]
    public void Without_a_process_start_nothing_goes()
    {
        string[] lines = [Locator(1234, "09:45:01"), Updater(600, "10:30:00", "in class, but which day?")];

        Assert.Empty(Cut(lines, [], now: At(19, 12, 0)));
    }

    [Fact]
    public void The_same_time_of_day_on_another_day_never_passes_for_the_window()
    {
        // Friday's lesson at the same hours, a weekend with the laptop off, then
        // Monday's lines from the same agent process (no new start on Monday).
        string[] lines =
        [
            Locator(1234, "09:45:01"),
            Updater(600, "10:30:00", "Friday, in class"),
            Agent(1234, "10:20:00", "Monday, but the log can't tell it from Saturday"),
        ];

        var kept = Cut(lines, [new AgentProcessStart(1234, At(16, 9, 45))], now: At(19, 12, 0));

        Assert.Empty(kept);
    }

    [Fact]
    public void A_day_off_between_the_window_and_the_next_line_leaves_the_windows_lines_out()
    {
        // Uploaded on Wednesday: from Wednesday's lines alone, Monday's in-class
        // line could as well be Tuesday's. Losing it is the price of never
        // sending a line from outside the window.
        string[] lines =
        [
            Locator(1234, "09:45:01"),
            Updater(600, "10:30:00", "Monday, in class"),
            Locator(4321, "09:00:00"),
        ];
        AgentProcessStart[] starts =
        [
            new(1234, At(19, 9, 45)),
            new(4321, At(21, 9, 0)),
        ];

        var kept = Cut(lines, starts, now: At(21, 9, 5));

        Assert.Equal([Locator(1234, "09:45:01")], kept);
    }

    [Fact]
    public void A_reused_process_id_pins_each_start_to_its_own_line()
    {
        // Windows gave the agent process id 1234 on Friday and again on Monday.
        string[] lines =
        [
            Locator(1234, "09:45:30"),
            Updater(600, "10:30:00", "Friday, in class"),
            Locator(1234, "09:45:01"),
            Updater(700, "10:31:00", "Monday, in class"),
        ];
        AgentProcessStart[] starts =
        [
            new(1234, At(16, 9, 45, 29)),
            new(1234, At(19, 9, 45)),
        ];

        var kept = Cut(lines, starts, now: At(19, 12, 0));

        Assert.Equal([Locator(1234, "09:45:01"), Updater(700, "10:31:00", "Monday, in class")], kept);
    }

    [Fact]
    public void A_pin_that_contradicts_the_order_of_the_file_leaves_the_whole_file_out()
    {
        // Two starts whose locator lines appear in the opposite order: one pin
        // must be wrong, so nothing can be trusted.
        string[] lines =
        [
            Locator(2222, "10:40:00"),
            Updater(600, "10:41:00", "in class?"),
            Locator(1111, "09:45:00"),
        ];
        AgentProcessStart[] starts =
        [
            new(2222, At(19, 10, 40)),
            new(1111, At(19, 9, 45)),
        ];

        Assert.Empty(Cut(lines, starts, now: At(19, 12, 0)));
    }

    [Fact]
    public void A_locator_line_long_after_the_start_does_not_pin_it()
    {
        // Same process id, but the line is hours after the logged start: not
        // that start's locator line (a reused id, or an older file).
        string[] lines = [Locator(1234, "10:30:00"), Updater(600, "10:31:00", "in class?")];

        Assert.Empty(Cut(lines, [new AgentProcessStart(1234, At(19, 9, 45))], now: At(19, 12, 0)));
    }

    [Fact]
    public void A_line_in_the_last_second_before_the_windows_end_only_goes_when_that_whole_second_is_inside()
    {
        var window = Monday with { To = At(19, 10, 30, 0).AddMilliseconds(500) };
        string[] lines =
        [
            Locator(1234, "09:45:01"),
            Updater(600, "10:29:59", "inside: written before 10:30:00"),
            Updater(600, "10:30:00", "maybe after 10:30:00.500"),
        ];

        var kept = Cut(lines, [new AgentProcessStart(1234, At(19, 9, 45))], now: At(19, 12, 0), window);

        Assert.Equal([Locator(1234, "09:45:01"), Updater(600, "10:29:59", "inside: written before 10:30:00")], kept);
    }
}
