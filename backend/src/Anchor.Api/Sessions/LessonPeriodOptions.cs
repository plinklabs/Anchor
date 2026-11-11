namespace Anchor.Api.Sessions;

/// <summary>
/// How a school's lesson periods turn into a session's planned end (#447).
/// </summary>
public sealed class LessonPeriodOptions
{
    public const string SectionName = "LessonPeriods";

    /// <summary>
    /// The default <see cref="RolloverWindow"/>: a session started in the last
    /// five minutes of a period belongs to the next one.
    /// </summary>
    public static readonly TimeSpan DefaultRolloverWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The time zone the periods' times are in: every school is in Belgium.
    /// An IANA id, which .NET resolves on Windows and Linux alike.
    /// </summary>
    public string TimeZone { get; set; } = "Europe/Brussels";

    /// <summary>
    /// A session started this close to the end of a period (or closer) counts
    /// as started in the next period: "end of this period" then means the end
    /// of the next one, and "end of next period" the end of the one after
    /// that. A teacher who starts at 15:28 for a period that ends at 15:30
    /// means the lesson that follows, not the two minutes left. Zero turns the
    /// rollover off.
    /// </summary>
    public TimeSpan RolloverWindow { get; set; } = DefaultRolloverWindow;

    /// <summary>The <see cref="TimeZone"/>, resolved.</summary>
    public TimeZoneInfo ResolveTimeZone() => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
}
