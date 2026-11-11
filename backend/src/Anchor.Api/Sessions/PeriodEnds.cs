namespace Anchor.Api.Sessions;

/// <summary>
/// The planned ends a session starting at a given moment can take from its
/// school's lesson periods (#447): the end of the period it starts in, and the
/// end of the period after that (a double period). Null where there is no such
/// period: the school has no timetable, the session starts outside every
/// period, or the period is the day's last.
/// </summary>
public sealed record PeriodEnds(DateTimeOffset? ThisPeriod, DateTimeOffset? NextPeriod)
{
    public static readonly PeriodEnds None = new(null, null);

    /// <summary>
    /// The period ends for a session starting at <paramref name="now"/>.
    /// <paramref name="periods"/> are school-local times of day in
    /// <paramref name="zone"/>, one timetable for every day: the period the
    /// session starts in is looked up on the school-local clock, and its end
    /// is turned into an instant on that day, so the result follows the
    /// zone's daylight-saving time.
    ///
    /// <para>
    /// A start within <paramref name="rolloverWindow"/> of its period's end
    /// counts as a start in the next period: that lesson is the one the teacher
    /// means. "This period" is then the next one, and "next period" the one
    /// after it.
    /// </para>
    /// </summary>
    public static PeriodEnds For(
        IEnumerable<(TimeOnly Start, TimeOnly End)> periods,
        DateTimeOffset now,
        TimeZoneInfo zone,
        TimeSpan rolloverWindow)
    {
        var ordered = periods.OrderBy(p => p.Start).ToList();
        var local = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        var day = DateOnly.FromDateTime(local);
        var time = TimeOnly.FromDateTime(local);

        var current = ordered.FindIndex(p => p.Start <= time && time < p.End);
        if (current < 0)
            return None;

        if (ordered[current].End - time <= rolloverWindow)
            current++;

        return new PeriodEnds(
            current < ordered.Count ? Instant(day, ordered[current].End, zone) : null,
            current + 1 < ordered.Count ? Instant(day, ordered[current + 1].End, zone) : null);
    }

    /// <summary>
    /// The instant a school-local wall-clock time on <paramref name="day"/>
    /// happens: 10:05 is 08:05 UTC in summer and 09:05 UTC in winter in
    /// Brussels. Lessons never fall in the night-time hour a clock change
    /// skips or repeats; if one did, the zone's standard offset applies.
    /// </summary>
    private static DateTimeOffset Instant(DateOnly day, TimeOnly time, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
