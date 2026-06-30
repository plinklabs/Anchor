using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Anchor.Api.Controllers;

/// <summary>
/// Admin-only read surface backing the "Manage admins" sub-tab (#300): list the
/// current admins, and search the users eligible to be promoted. The actual
/// promote/demote mutations live on <see cref="MeController"/> (<c>/me/promote</c>,
/// <c>/me/demote</c>) so the role-changing verbs stay in one place.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.Admin)]
[Route("admin/users")]
public sealed class AdminUsersController : ControllerBase
{
    // A typed-search picker only needs a handful of matches at a time; cap the
    // result set so a short query against a large directory can't return the
    // whole user table.
    private const int MaxCandidateResults = 20;

    private readonly AnchorDbContext _db;

    public AdminUsersController(AnchorDbContext db)
    {
        _db = db;
    }

    /// <summary>The current admins, name-ordered, for the manage-admins list.</summary>
    [HttpGet("admins")]
    [ProducesResponseType(typeof(IReadOnlyList<AdminUserResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<AdminUserResponse>>> ListAdmins(
        CancellationToken cancellationToken)
    {
        var admins = await _db.Users.AsNoTracking()
            .Where(u => u.Role == UserRole.Admin)
            .OrderBy(u => u.DisplayName)
            .Select(u => new AdminUserResponse(u.Id, u.DisplayName, u.EntraOid, u.Role))
            .ToListAsync(cancellationToken);

        return Ok(admins);
    }

    /// <summary>
    /// Search non-admin users who have signed in at least once, for the "add
    /// admin" picker. A user only exists in the DB after their first sign-in, so
    /// an empty result is the expected signal that the person still needs to log
    /// into the dashboard before they can be promoted.
    /// </summary>
    [HttpGet("candidates")]
    [ProducesResponseType(typeof(IReadOnlyList<AdminUserResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<AdminUserResponse>>> SearchCandidates(
        [FromQuery] string? query,
        CancellationToken cancellationToken)
    {
        var users = _db.Users.AsNoTracking().Where(u => u.Role != UserRole.Admin);

        var trimmed = query?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            // Lowercase both sides so the match is case-insensitive regardless of
            // the column collation (SQLite in tests vs. SQL Server in prod).
            var needle = trimmed.ToLower();
            users = users.Where(u => u.DisplayName.ToLower().Contains(needle));
        }

        var results = await users
            .OrderBy(u => u.DisplayName)
            .Take(MaxCandidateResults)
            .Select(u => new AdminUserResponse(u.Id, u.DisplayName, u.EntraOid, u.Role))
            .ToListAsync(cancellationToken);

        return Ok(results);
    }
}

public sealed record AdminUserResponse(Guid Id, string DisplayName, Guid EntraOid, UserRole Role);
