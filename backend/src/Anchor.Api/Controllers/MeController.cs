using System.Security.Claims;
using Anchor.Api.Auth;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Anchor.Api.Controllers;

[ApiController]
[Authorize]
[Route("me")]
public sealed class MeController : ControllerBase
{
    private readonly IUserStore _userStore;
    private readonly AnchorDbContext _db;
    private readonly IWebHostEnvironment _env;

    public MeController(IUserStore userStore, AnchorDbContext db, IWebHostEnvironment env)
    {
        _userStore = userStore;
        _db = db;
        _env = env;
    }

    [HttpGet]
    public async Task<ActionResult<MeResponse>> Get(
        [FromQuery(Name = "promote-me")] string? promoteMe,
        CancellationToken cancellationToken)
    {
        if (!User.TryGetEntraOid(out var entraOid))
            return Unauthorized();

        // The JWT carries the underlying Teacher/Student claim from Entra; the
        // Admin claim (if any) is layered on by AdminRoleClaimsTransformation
        // after a DB lookup. We use the JWT-supplied role as the upsert input
        // so the DB row tracks the real identity, while the response role
        // reflects whatever DB row (incl. an Admin promotion) wins.
        UserRole role;
        if (User.IsInRole("Teacher")) role = UserRole.Teacher;
        else if (User.IsInRole("Student")) role = UserRole.Student;
        else if (User.IsInRole("Admin")) role = UserRole.Teacher; // admin without an underlying claim — treat as teacher
        else return Forbid();

        var displayName = User.FindFirst("name")?.Value
                          ?? User.FindFirst(ClaimTypes.Name)?.Value
                          ?? User.Identity?.Name
                          ?? "Unknown";

        var user = await _userStore.UpsertAsync(entraOid, displayName, role, cancellationToken);

        if (_env.IsDevelopment() && user.Role != UserRole.Admin)
        {
            // Two dev-only auto-promotion paths so the catalogue editor is
            // reachable without SQL or a hand-typed URL (#75):
            //
            //  - Explicit: ?promote-me=admin (the documented escape hatch).
            //  - Implicit bootstrap: if no admins exist in the DB yet, the
            //    first real (non-impersonated) caller becomes admin. The
            //    impersonation header is excluded so a dev running headless
            //    verify scripts as the seeded Dev Teacher doesn't accidentally
            //    burn the bootstrap on a synthetic identity, leaving the
            //    actual developer unable to be promoted later.
            //
            // Both branches are gated on IsDevelopment so a misconfigured prod
            // deployment physically cannot promote a stranger.
            var isImpersonating = string.Equals(
                User.Identity?.AuthenticationType,
                DevImpersonationAuthHandler.SchemeName,
                StringComparison.Ordinal);
            var explicitOptIn = string.Equals(promoteMe, "admin", StringComparison.OrdinalIgnoreCase);
            var bootstrapEligible = !isImpersonating
                && !await _db.Users.AnyAsync(u => u.Role == UserRole.Admin, cancellationToken);

            if (explicitOptIn || bootstrapEligible)
            {
                var tracked = await _db.Users.FirstAsync(u => u.Id == user.Id, cancellationToken);
                tracked.Role = UserRole.Admin;
                await _db.SaveChangesAsync(cancellationToken);
                user = tracked;
            }
        }

        if (_env.IsDevelopment() && role == UserRole.Teacher)
        {
            await DevDataSeeder.EnsureDevTeacherMembershipAsync(_db, user.Id, cancellationToken);
        }

        return new MeResponse(user.Id, user.EntraOid, user.DisplayName, user.Role);
    }

    /// <summary>
    /// Admin-only: promote another user (referenced by their internal user id)
    /// to <see cref="UserRole.Admin"/>. Required for the school-policy follow-up
    /// where the first dev admin grants further admins without DB surgery.
    /// Full multi-admin governance (audit log, revocation flows) is deferred.
    /// </summary>
    [HttpPost("promote")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(typeof(MeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MeResponse>> PromoteToAdmin(
        [FromBody] PromoteToAdminRequest request,
        CancellationToken cancellationToken)
    {
        var target = await _db.Users.FirstOrDefaultAsync(u => u.Id == request.UserId, cancellationToken);
        if (target is null)
            return NotFound();

        if (target.Role != UserRole.Admin)
        {
            target.Role = UserRole.Admin;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return new MeResponse(target.Id, target.EntraOid, target.DisplayName, target.Role);
    }
}

public sealed record MeResponse(Guid Id, Guid EntraOid, string DisplayName, UserRole Role);

public sealed record PromoteToAdminRequest(Guid UserId);
