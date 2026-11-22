using FocusAgent.Core.Diagnostics;
using FocusAgent.WitnessHost;

namespace FocusAgent.WitnessHost.Tests;

/// <summary>
/// The witness host's own log (#561): one file a day in the agent's log folder,
/// in the agent log's format so the log upload cuts it to a session window, and
/// never in the host's way.
/// </summary>
public sealed class WitnessHostLogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "anchor-witness-host-log-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Writes_each_line_in_the_agent_log_format_to_the_day_s_file()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 20, 8, 31, 2, 45, TimeSpan.FromHours(1)));
        var log = new WitnessHostLog(_folder, clock, processId: 4242);

        log.Info("Witness host 0.6.0 started by chrome-extension://dnkimhodjfogjibnbbfdjdapgmmiojio/; agent pipe anchor-witness.");
        log.Warn("Connecting to the agent's pipe anchor-witness failed (attempt 1):\r\nsecond line");

        var lines = File.ReadAllLines(Path.Combine(_folder, "witness-host-20261120.log"));
        Assert.Equal(
        [
            "2026-11-20 08:31:02.045 +01:00 [INF] WitnessHost[4242]: Witness host 0.6.0 started by chrome-extension://dnkimhodjfogjibnbbfdjdapgmmiojio/; agent pipe anchor-witness.",
            "2026-11-20 08:31:02.045 +01:00 [WRN] WitnessHost[4242]: Connecting to the agent's pipe anchor-witness failed (attempt 1): second line",
        ], lines);
        // What the log upload reads it with.
        var entries = AgentLogCutter.ParseAgentLog(lines);
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(clock.Now, e.At));
    }

    [Fact]
    public void Two_hosts_append_to_the_same_file()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.FromHours(1)));
        var first = new WitnessHostLog(_folder, clock, processId: 1);
        var second = new WitnessHostLog(_folder, clock, processId: 2);

        first.Info("one");
        second.Info("two");
        first.Info("three");

        Assert.Equal(
            ["WitnessHost[1]: one", "WitnessHost[2]: two", "WitnessHost[1]: three"],
            File.ReadAllLines(Path.Combine(_folder, "witness-host-20261120.log")).Select(l => l[(l.IndexOf("] ") + 2)..]));
    }

    [Fact]
    public void Keeps_the_newest_daily_files()
    {
        Directory.CreateDirectory(_folder);
        var days = Enumerable.Range(1, WitnessHostLog.KeptFiles + 3).Select(d => new DateTime(2026, 10, d)).ToList();
        foreach (var day in days)
            File.WriteAllText(Path.Combine(_folder, $"witness-host-{day:yyyyMMdd}.log"), "x");
        // Not the host's: left alone.
        File.WriteAllText(Path.Combine(_folder, "focusagent-20261001.log"), "x");
        File.WriteAllText(Path.Combine(_folder, "witness-host-notes.log"), "x");

        new WitnessHostLog(_folder).DeleteOldFiles();

        var left = Directory.GetFiles(_folder).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(
            new[] { "focusagent-20261001.log", "witness-host-notes.log" }
                .Concat(days.Skip(3).Select(d => $"witness-host-{d:yyyyMMdd}.log"))
                .Order(),
            left);
    }

    [Fact]
    public void A_log_that_cannot_be_written_never_throws()
    {
        // The folder's name is taken by a file.
        File.WriteAllText(_folder, "in the way");
        try
        {
            var log = new WitnessHostLog(_folder);
            Assert.Null(Record.Exception(() => log.Warn("dropped")));
            Assert.Null(Record.Exception(log.DeleteOldFiles));
        }
        finally
        {
            File.Delete(_folder);
        }
    }

    [Fact]
    public void Without_a_folder_it_writes_nothing()
    {
        WitnessHostLog.None.Info("nowhere");
        new WitnessHostLog(null).Warn("nowhere");

        Assert.False(Directory.Exists(_folder));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; } = now;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone { get; } =
            TimeZoneInfo.CreateCustomTimeZone("test", now.Offset, "test", "test");
    }
}
