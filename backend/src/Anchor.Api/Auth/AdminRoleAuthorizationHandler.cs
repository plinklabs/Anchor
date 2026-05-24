using Anchor.Domain.Users;
using Microsoft.AspNetCore.Authorization;

namespace Anchor.Api.Auth;

/// <summary>
/// Authorization handler that satisfies <see cref="AdminRoleRequirement"/>
/// either via a direct "Admin" role claim or by looking the caller up in the
/// user store and checking the DB-side role (#75).
///
/// Admin is a DB-only designation today: Entra (real JWT) does not currently
/// mint an "Admin" app role, so a freshly-issued bearer token never carries
/// the claim even for users who were promoted via the dev /me?promote-me=admin
/// flow. Doing the DB lookup here (instead of an IClaimsTransformation that
/// runs on every authenticated request) keeps the per-connection user-store
/// cache on SessionHub intact (#55) — only admin-protected endpoints pay the
/// extra read.
///
/// The DevImpersonation handler emits the role directly from the DB so the
/// short-circuit on <c>IsInRole("Admin")</c> covers headless callers without
/// a second lookup.
/// </summary>
public sealed class AdminRoleAuthorizationHandler : AuthorizationHandler<AdminRoleRequirement>
{
    private readonly IUserStore _users;

    public AdminRoleAuthorizationHandler(IUserStore users)
    {
        _users = users;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AdminRoleRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true) return;

        if (context.User.IsInRole("Admin"))
        {
            context.Succeed(requirement);
            return;
        }

        if (!context.User.TryGetEntraOid(out var oid)) return;

        var user = await _users.FindByEntraOidAsync(oid);
        if (user is not null && user.Role == UserRole.Admin)
            context.Succeed(requirement);
    }
}

public sealed class AdminRoleRequirement : IAuthorizationRequirement;
