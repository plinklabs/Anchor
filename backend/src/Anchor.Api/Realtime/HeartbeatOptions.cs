namespace Anchor.Api.Realtime;

public sealed class HeartbeatOptions
{
    public const string SectionName = "Heartbeat";

    public int IntervalSeconds { get; set; } = 10;
    public int TimeoutMultiplier { get; set; } = 2;
    public int ScanIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// When false, <see cref="HeartbeatMonitor"/> is not registered as a hosted
    /// service. Integration tests share a single in-memory SQLite connection
    /// across the process; a periodic background scan racing the test's
    /// request-handling code surfaces as <c>database is locked</c>. Tests that
    /// need to exercise the monitor call <c>ScanOnceAsync</c> directly.
    /// </summary>
    public bool EnableMonitor { get; set; } = true;

    public TimeSpan Interval => TimeSpan.FromSeconds(IntervalSeconds);
    public TimeSpan Timeout => TimeSpan.FromSeconds(IntervalSeconds * TimeoutMultiplier);
    public TimeSpan ScanInterval => TimeSpan.FromSeconds(ScanIntervalSeconds);
}
