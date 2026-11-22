namespace Anchor.Api.Diagnostics;

/// <summary>
/// The records of admins' downloads of the agents' log uploads (#566). They
/// are kept far longer than the logs themselves
/// (<see cref="LogUploadOptions.RetentionDays"/>): what matters about a
/// download, who opened which student's log and why, matters after the log is
/// gone, when the school's leadership or DPO reviews the access or a fraud
/// decision is contested.
/// </summary>
public sealed class LogAccessOptions
{
    public const string SectionName = "LogAccess";

    /// <summary>
    /// Records are deleted this many days after the download, by
    /// <see cref="LogUploadPruner"/>, in the same sweep as the uploads.
    /// </summary>
    public int RetentionDays { get; set; } = 365;

    public TimeSpan Retention => TimeSpan.FromDays(Math.Max(0, RetentionDays));
}
