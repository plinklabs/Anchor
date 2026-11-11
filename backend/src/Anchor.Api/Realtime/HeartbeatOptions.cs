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

    /// <summary>
    /// How long a joined participant's agent has to send its first heartbeat
    /// before the teacher's roster shows it as missing (#444). The agent and the
    /// Edge extension each join on their own, so a join alone doesn't say the
    /// agent is there; only its heartbeats do. The wait starts when the agent is
    /// first expected: the participant's join, or, after a backend restart, the
    /// extension's first ping. It has to cover the agent's 5-second join toast
    /// and its first ping one <see cref="IntervalSeconds"/> after it joins.
    /// </summary>
    public int AgentJoinGraceSeconds { get; set; } = 30;

    public TimeSpan Interval => TimeSpan.FromSeconds(IntervalSeconds);
    public TimeSpan Timeout => TimeSpan.FromSeconds(IntervalSeconds * TimeoutMultiplier);
    public TimeSpan AgentJoinGrace => TimeSpan.FromSeconds(AgentJoinGraceSeconds);
    public TimeSpan ScanInterval => TimeSpan.FromSeconds(ScanIntervalSeconds);
}
