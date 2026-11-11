namespace Anchor.Api.Diagnostics;

/// <summary>
/// The agents' log uploads of the test phase (#446): each agent uploads its log
/// lines from a window around each session its student was on the roster of,
/// and nothing else. Off by default. Switched off, the backend reports no
/// windows to upload, so turning it off needs no agent release.
/// </summary>
public sealed class LogUploadOptions
{
    public const string SectionName = "LogUpload";

    /// <summary>
    /// The switch. While off, <c>GET /log-uploads/pending</c> reports no windows
    /// and uploads are refused. Uploads already stored stay until they are
    /// pruned.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// How long before a session started its window opens. The agent usually
    /// starts well before class, and what goes wrong then (an update, the
    /// sign-in) shows up there.
    /// </summary>
    public TimeSpan WindowBefore { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How long after a session ended its window closes.</summary>
    public TimeSpan WindowAfter { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Only sessions that started at most this many days ago are asked for. The
    /// agent keeps its own logs for 14 days, so older windows are gone from the
    /// laptop anyway.
    /// </summary>
    public int LookbackDays { get; set; } = 14;

    /// <summary>
    /// Uploads are deleted this many days after they arrived, by
    /// <see cref="LogUploadPruner"/>, at the event pruner's quiet hour.
    /// </summary>
    public int RetentionDays { get; set; } = 14;

    /// <summary>
    /// The largest upload (gzipped) the backend takes for one window. The agent
    /// is told and leaves lines out of the middle of the window to fit. A
    /// classroom laptop logs a few MB a day, about 5% of that after gzip, and a
    /// window is a few hours of it, so 1 MiB leaves plenty of room.
    /// </summary>
    public int MaxUploadBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// Mirrors <see cref="Events.EventRetentionOptions.EnablePruner"/>: when
    /// false, <see cref="LogUploadPruner"/> is not registered as a hosted
    /// service, and tests drive <see cref="LogUploadPruner.PruneOnceAsync"/>
    /// directly. Independent of <see cref="Enabled"/>: uploads stored while the
    /// switch was on are still deleted after it is turned off.
    /// </summary>
    public bool EnablePruner { get; set; } = true;

    // Floored at one byte: a zero cap would refuse every upload while asking for them.
    public int EffectiveMaxUploadBytes => Math.Max(1, MaxUploadBytes);

    public TimeSpan Lookback => TimeSpan.FromDays(Math.Max(0, LookbackDays));

    public TimeSpan Retention => TimeSpan.FromDays(Math.Max(0, RetentionDays));
}
