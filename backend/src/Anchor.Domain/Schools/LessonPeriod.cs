namespace Anchor.Domain.Schools;

/// <summary>
/// One lesson period of a school's timetable (#447), e.g. the first period
/// 08:25–09:15. An admin enters them per school; a teacher starting a session
/// can then end it at the end of the current or the next period.
///
/// <para>
/// One timetable applies to every school day. The times are the school's
/// wall-clock times, not instants: 08:25 stays 08:25 when the clocks change,
/// so they're stored as a time of day and turned into an instant on the day
/// they're used, in the school's time zone.
/// </para>
/// </summary>
public sealed class LessonPeriod
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required Guid SchoolId { get; init; }

    /// <summary>When the period starts, school-local.</summary>
    public required TimeOnly Start { get; init; }

    /// <summary>When the period ends, school-local. Always after <see cref="Start"/>.</summary>
    public required TimeOnly End { get; init; }
}
