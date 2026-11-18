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

    /// <summary>
    /// How often, during a session, the agent looks for an Edge profile that is
    /// open with the extension switched off or removed (#518): the case the
    /// witness link can't show while another profile's extension is connected.
    /// </summary>
    public int ExtensionProfileScanSeconds { get; init; } = 5;

    /// <summary>
    /// How long, during a session, the extension may report that it can't work
    /// on all sites before the teacher is told (#524). Edge is paused at once, and
    /// the pause says how to put "Site access" back; a student who does so in time
    /// isn't reported.
    /// </summary>
    public int ExtensionSiteAccessReportSeconds { get; init; } = 60;

    /// <summary>
    /// How often, during a session, the agent looks up the Edge profile signed in
    /// with the school account and the Edge windows outside it (#525): a window of
    /// another profile, a Guest window or an InPrivate window is paused within this
    /// long of opening.
    /// </summary>
    public int SchoolProfileScanSeconds { get; init; } = 2;

    /// <summary>
    /// How long a window outside the school profile may stay open, or Edge may lack
    /// a profile signed in with the school account, before the teacher is told
    /// (#525). The pause says what to do; a student who does it in time isn't
    /// reported.
    /// </summary>
    public int SchoolProfileReportSeconds { get; init; } = 60;
}
