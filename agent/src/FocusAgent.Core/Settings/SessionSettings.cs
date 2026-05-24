using FocusAgent.Core.Focus;

namespace FocusAgent.Core.Settings;

public sealed record SessionSettings
{
    public const string SectionName = "Session";

    public List<AllowedAppRule> AllowedApps { get; init; } = new();
    public TimeSpan DuplicateCoalesceWindow { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Interval between application-level Heartbeat pings sent to the backend
    /// while the agent is in an active session. The backend's HeartbeatMonitor
    /// declares the agent stale at <c>2 × IntervalSeconds</c> (default 20s) and
    /// emits a HeartbeatLost event.
    /// </summary>
    public int HeartbeatIntervalSeconds { get; init; } = 10;
}
