namespace Anchor.Api.Events;

public sealed class EventRetentionOptions
{
    public const string SectionName = "EventRetention";

    /// <summary>
    /// Raw <see cref="Domain.Events.Event"/> rows older than this are eligible
    /// for pruning once their parent session has ended. Per-session summaries
    /// are kept indefinitely.
    /// </summary>
    public int RawEventDays { get; set; } = 30;

    /// <summary>
    /// How often <see cref="EventPruner"/> wakes up. 30 days is the cutoff so
    /// daily is plenty — pruning N hours late costs nothing, the point is
    /// bounded growth, not freshness (#77).
    /// </summary>
    public int PruneIntervalMinutes { get; set; } = 1440;

    /// <summary>
    /// Rows-per-round on the batched delete. A first-time prune against a
    /// backlog of months of events shouldn't hold a long write lock against
    /// concurrent event inserts; smaller batches give the writers room.
    /// </summary>
    public int BatchSize { get; set; } = 10_000;

    /// <summary>
    /// Mirrors <see cref="Realtime.HeartbeatOptions.EnableMonitor"/> — tests
    /// share a single in-memory SQLite connection across the process, so the
    /// periodic background loop is disabled in <c>AnchorApiFactory</c> and
    /// the pruner is exercised via <c>PruneOnceAsync</c> directly.
    /// </summary>
    public bool EnablePruner { get; set; } = true;

    /// <summary>
    /// If a prune scan finds more than this many events under sessions that
    /// have <c>EndedAt = null</c> but are older than the retention window,
    /// the pruner logs a warning instead of silently leaving them. Indicates
    /// a session that was abandoned without End being called.
    /// </summary>
    public int OrphanedActiveSessionWarnThreshold { get; set; } = 100;

    public TimeSpan RawEventMaxAge => TimeSpan.FromDays(RawEventDays);
    public TimeSpan PruneInterval => TimeSpan.FromMinutes(PruneIntervalMinutes);
}
