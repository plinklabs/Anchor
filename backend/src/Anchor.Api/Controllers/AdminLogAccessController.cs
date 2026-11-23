using Anchor.Domain.Sessions;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Anchor.Api.Controllers;

/// <summary>
/// The access history of the agents' log uploads (#566): every admin download
/// (<see cref="AdminLogUploadsController.Download"/>), with who, when, which
/// session and student, the purpose, the reason and who asked, so the school's
/// leadership or DPO can review it. Read-only on purpose: the API offers no way
/// to change or delete a record; only the pruner does, after
/// <c>LogAccess:RetentionDays</c>.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.Admin)]
[Route("admin/log-access")]
public sealed class AdminLogAccessController : ControllerBase
{
    private readonly AnchorDbContext _db;

    public AdminLogAccessController(AnchorDbContext db)
    {
        _db = db;
    }

    /// <summary>Every record, newest first.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<LogAccessRecord>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<LogAccessRecord>>> List(CancellationToken cancellationToken)
    {
        var records = await _db.LogAccesses.AsNoTracking()
            .Select(a => new LogAccessRecord(
                a.Id,
                a.AccessedAt,
                a.AdminUserId,
                a.AdminDisplayName,
                a.SessionId,
                a.SessionStartedAt,
                a.ClassName,
                a.StudentUserId,
                a.StudentDisplayName,
                a.Purpose,
                a.Reason,
                a.RequestedBy))
            .ToListAsync(cancellationToken);

        // SQLite (dev + tests) can't ORDER BY a DateTimeOffset server side; a
        // year of downloads is a short list.
        return Ok(records.OrderByDescending(r => r.AccessedAt).ThenBy(r => r.StudentDisplayName).ToList());
    }
}

/// <summary>One admin's download of one student's log upload (#566).</summary>
public sealed record LogAccessRecord(
    Guid Id,
    DateTimeOffset AccessedAt,
    Guid AdminUserId,
    string AdminDisplayName,
    Guid SessionId,
    DateTimeOffset SessionStartedAt,
    string ClassName,
    Guid StudentUserId,
    string StudentDisplayName,
    LogAccessPurpose Purpose,
    string Reason,
    string? RequestedBy);
