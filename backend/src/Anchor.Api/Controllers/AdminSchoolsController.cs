using System.Globalization;
using Anchor.Api.Schools;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Anchor.Api.Controllers;

/// <summary>
/// Admin-only surface backing the "Schools" sub-tab (#301): list every school
/// (Entra company) with its active state and lesson periods, toggle activation,
/// and enter the lesson periods (#447). Only active schools are shown to
/// teachers in the Classes school selector (<see cref="DirectoryController"/>);
/// a school's lesson periods let its teachers end a session at the end of a
/// period.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.Admin)]
[Route("admin/schools")]
public sealed class AdminSchoolsController : ControllerBase
{
    /// <summary>More than a school day holds, so a pasted mistake can't flood the table.</summary>
    public const int MaxLessonPeriods = 20;

    private readonly ISchoolDirectory _schools;
    private readonly ILogger<AdminSchoolsController> _logger;

    public AdminSchoolsController(ISchoolDirectory schools, ILogger<AdminSchoolsController> logger)
    {
        _schools = schools;
        _logger = logger;
    }

    /// <summary>
    /// Every known school with its active state, name-ordered. Degrades
    /// gracefully when Graph is unavailable (#281): rather than 502, fall back to
    /// the persisted rows so an admin can still review and toggle the schools the
    /// system has already seen.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<SchoolResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<SchoolResponse>>> List(CancellationToken cancellationToken)
    {
        try
        {
            var schools = await _schools.ListAllAsync(cancellationToken);
            return Ok(schools.Select(SchoolResponse.From).ToList());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Listing schools from directory failed; returning persisted schools only");
            var persisted = await _schools.ListPersistedAsync(cancellationToken);
            return Ok(persisted.Select(SchoolResponse.From).ToList());
        }
    }

    /// <summary>Activate or deactivate a school. Persists the state, creating the
    /// row on first toggle. Works even when Graph is down.</summary>
    [HttpPost("activation")]
    [ProducesResponseType(typeof(SchoolResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SchoolResponse>> SetActivation(
        [FromBody] SetSchoolActivationRequest request,
        CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            return BadRequest(new ProblemDetails { Title = "A school name is required." });

        var state = await _schools.SetActiveAsync(name, request.IsActive, cancellationToken);
        return Ok(SchoolResponse.From(state));
    }

    /// <summary>
    /// Replace a school's lesson periods (#447): one timetable for every school
    /// day, as school-local times ("08:25"). Each period must end after it
    /// starts, and no two may overlap; back to back is fine. An empty list
    /// clears the timetable. Creates the school's row on first use, like the
    /// activation toggle. Works even when Graph is down.
    /// </summary>
    [HttpPut("lesson-periods")]
    [ProducesResponseType(typeof(SchoolResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SchoolResponse>> SetLessonPeriods(
        [FromBody] SetSchoolLessonPeriodsRequest request,
        CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            return BadRequest(new ProblemDetails { Title = "A school name is required." });

        var requested = request.LessonPeriods ?? Array.Empty<LessonPeriodDto>();
        if (requested.Count > MaxLessonPeriods)
            return BadRequest(new ProblemDetails { Title = $"A school can have at most {MaxLessonPeriods} lesson periods." });

        var periods = new List<SchoolLessonPeriod>(requested.Count);
        foreach (var period in requested)
        {
            if (!LessonPeriodDto.TryParseTime(period.Start, out var start) ||
                !LessonPeriodDto.TryParseTime(period.End, out var end))
                return BadRequest(new ProblemDetails { Title = "Lesson period times must be like 08:25." });
            if (end <= start)
                return BadRequest(new ProblemDetails { Title = "A lesson period must end after it starts." });
            periods.Add(new SchoolLessonPeriod(start, end));
        }

        var ordered = periods.OrderBy(p => p.Start).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Start < ordered[i - 1].End)
                return BadRequest(new ProblemDetails { Title = "Lesson periods must not overlap." });
        }

        var state = await _schools.SetLessonPeriodsAsync(name, ordered, cancellationToken);
        return Ok(SchoolResponse.From(state));
    }
}

public sealed record SchoolResponse(string Name, bool IsActive, IReadOnlyList<LessonPeriodDto> LessonPeriods)
{
    public static SchoolResponse From(SchoolState state) =>
        new(state.Name, state.IsActive, state.LessonPeriods.Select(LessonPeriodDto.From).ToList());
}

/// <summary>
/// A lesson period on the wire (#447): its school-local start and end as
/// "HH:mm", in the school's time zone. One timetable for every school day.
/// </summary>
public sealed record LessonPeriodDto(string Start, string End)
{
    private static readonly string[] TimeFormats = ["H:mm", "HH:mm"];

    public static LessonPeriodDto From(SchoolLessonPeriod period) =>
        new(Format(period.Start), Format(period.End));

    public static bool TryParseTime(string? value, out TimeOnly time) =>
        TimeOnly.TryParseExact(
            value?.Trim(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private static string Format(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);
}

public sealed record SetSchoolActivationRequest(string? Name, bool IsActive);

/// <summary>A school's whole timetable (#447), replacing the one it had.</summary>
public sealed record SetSchoolLessonPeriodsRequest(string? Name, IReadOnlyList<LessonPeriodDto>? LessonPeriods);
