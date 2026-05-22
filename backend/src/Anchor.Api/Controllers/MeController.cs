using System.Security.Claims;
using Anchor.Domain.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Anchor.Api.Controllers;

[ApiController]
[Authorize]
[Route("me")]
public sealed class MeController : ControllerBase
{
    private const string EntraOidShortClaim = "oid";
    private const string EntraOidLongClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    private readonly IUserStore _userStore;

    public MeController(IUserStore userStore)
    {
        _userStore = userStore;
    }

    [HttpGet]
    public async Task<ActionResult<MeResponse>> Get(CancellationToken cancellationToken)
    {
        var oidValue = User.FindFirst(EntraOidShortClaim)?.Value
                       ?? User.FindFirst(EntraOidLongClaim)?.Value;
        if (oidValue is null || !Guid.TryParse(oidValue, out var entraOid))
            return Unauthorized();

        UserRole role;
        if (User.IsInRole("Teacher")) role = UserRole.Teacher;
        else if (User.IsInRole("Student")) role = UserRole.Student;
        else return Forbid();

        var displayName = User.FindFirst("name")?.Value
                          ?? User.FindFirst(ClaimTypes.Name)?.Value
                          ?? User.Identity?.Name
                          ?? "Unknown";

        var user = await _userStore.UpsertAsync(entraOid, displayName, role, cancellationToken);
        return new MeResponse(user.Id, user.EntraOid, user.DisplayName, user.Role);
    }
}

public sealed record MeResponse(Guid Id, Guid EntraOid, string DisplayName, UserRole Role);
