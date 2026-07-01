using Anchor.Api.Schools;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Anchor.Api.Controllers;

/// <summary>
/// Admin-only surface backing the "Schools" sub-tab (#301): list every school
/// (Entra company) with its active state, and toggle activation. Only active
/// schools are shown to teachers in the Classes school selector
/// (<see cref="DirectoryController"/>).
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.Admin)]
[Route("admin/schools")]
public sealed class AdminSchoolsController : ControllerBase
{
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
            return Ok(schools.Select(s => new SchoolResponse(s.Name, s.IsActive)).ToList());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Listing schools from directory failed; returning persisted schools only");
            var persisted = await _schools.ListPersistedAsync(cancellationToken);
            return Ok(persisted.Select(s => new SchoolResponse(s.Name, s.IsActive)).ToList());
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
        return Ok(new SchoolResponse(state.Name, state.IsActive));
    }
}

public sealed record SchoolResponse(string Name, bool IsActive);

public sealed record SetSchoolActivationRequest(string? Name, bool IsActive);
