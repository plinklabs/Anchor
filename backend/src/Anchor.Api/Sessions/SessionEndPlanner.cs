using System.Text.Json.Serialization;
using Anchor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Anchor.Api.Sessions;

/// <summary>
/// How a teacher chose to end the session they start (#447). Serialized by
/// name on the wire, like the other wire enums.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SessionEndMode
{
    /// <summary>At the end of the lesson period the session starts in.</summary>
    ThisPeriod,

    /// <summary>At the end of the period after that one: a double period.</summary>
    NextPeriod,

    /// <summary>At a time the teacher picked.</summary>
    SpecificTime,
}

/// <summary>
/// Works out when a session ends (#447): the period ends its class's school
/// offers at a given moment, and the latest time a teacher may pick.
/// </summary>
public sealed class SessionEndPlanner
{
    private readonly AnchorDbContext _db;
    private readonly IOptionsMonitor<LessonPeriodOptions> _periodOptions;
    private readonly IOptionsMonitor<SessionAutoEndOptions> _autoEndOptions;

    public SessionEndPlanner(
        AnchorDbContext db,
        IOptionsMonitor<LessonPeriodOptions> periodOptions,
        IOptionsMonitor<SessionAutoEndOptions> autoEndOptions)
    {
        _db = db;
        _periodOptions = periodOptions;
        _autoEndOptions = autoEndOptions;
    }

    /// <summary>
    /// The period ends for a session of a class of <paramref name="schoolTag"/>
    /// starting at <paramref name="now"/>. A class belongs to the school whose
    /// name is its <c>SchoolTag</c>; a class without one, or a school without
    /// lesson periods, has none.
    /// </summary>
    public async Task<PeriodEnds> PeriodEndsAsync(string? schoolTag, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(schoolTag))
            return PeriodEnds.None;

        var periods = await _db.LessonPeriods.AsNoTracking()
            .Where(p => _db.Schools.Any(s => s.Id == p.SchoolId && s.Name == schoolTag))
            .Select(p => new { p.Start, p.End })
            .ToListAsync(cancellationToken);
        if (periods.Count == 0)
            return PeriodEnds.None;

        var options = _periodOptions.CurrentValue;
        return PeriodEnds.For(
            periods.Select(p => (p.Start, p.End)),
            now,
            options.ResolveTimeZone(),
            options.RolloverWindow);
    }

    /// <summary>
    /// The latest end a teacher may pick for a session starting at
    /// <paramref name="now"/>: the MaxDuration safety net (#345) would end it
    /// then anyway, so a later time would only promise what won't happen.
    /// Null when that safety net is off.
    /// </summary>
    public DateTimeOffset? LatestEndsAt(DateTimeOffset now)
    {
        var maxDuration = _autoEndOptions.CurrentValue.MaxDuration;
        return maxDuration > TimeSpan.Zero ? now + maxDuration : null;
    }
}
