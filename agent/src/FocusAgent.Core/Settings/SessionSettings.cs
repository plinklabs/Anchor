namespace FocusAgent.Core.Settings;

public sealed record SessionSettings
{
    public const string SectionName = "Session";

    public TimeSpan DuplicateCoalesceWindow { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Interval between application-level Heartbeat pings sent to the backend
    /// while the agent is in an active session. The backend's HeartbeatMonitor
    /// declares the agent stale at <c>2 × IntervalSeconds</c> (default 20s) and
    /// emits a HeartbeatLost event.
    /// </summary>
    public int HeartbeatIntervalSeconds { get; init; } = 10;

    /// <summary>
    /// Interval at which the agent's InPrivate witness (#148) re-scans the open
    /// Edge windows while in a joined session. A few seconds is responsive enough
    /// for a teacher's live roster without polling the window list tightly; the
    /// scan only reports each InPrivate window once, so a short interval doesn't
    /// produce repeat events.
    /// </summary>
    public int InPrivateScanIntervalSeconds { get; init; } = 5;

    /// <summary>
    /// How long the extension's witness link may be down, with Edge open, before
    /// the agent pauses Edge for the rest of the outage (#427). The link also drops
    /// when Edge restarts or the extension updates or reloads; those reconnect in a
    /// few seconds, so a short grace keeps a normal restart from being punished.
    /// </summary>
    public int ExtensionGraceSeconds { get; init; } = 10;

    /// <summary>
    /// How long Edge opens when a student whose Edge is paused asks to turn the
    /// extension back on (#427). Edge can't open its own <c>edge://extensions</c>
    /// page from outside, so the student needs Edge for a moment to switch Anchor
    /// on there; the pause lifts for good as soon as the extension reconnects, and
    /// returns when this window runs out without it.
    /// </summary>
    public int ExtensionRepairSeconds { get; init; } = 60;
}
