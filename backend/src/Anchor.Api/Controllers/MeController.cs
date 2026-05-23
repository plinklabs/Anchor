using System.Security.Claims;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
    public async Task<ActionResult<MeResponse>> Get(CancellationToken cancellationToken)
    {
        if (!User.TryGetEntraOid(out var entraOid))
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

        if (_env.IsDevelopment() && role == UserRole.Teacher)
        {
            await DevDataSeeder.EnsureDevTeacherMembershipAsync(_db, user.Id, cancellationToken);
        }

        return new MeResponse(user.Id, user.EntraOid, user.DisplayName, user.Role);
    }
}

public sealed record MeResponse(Guid Id, Guid EntraOid, string DisplayName, UserRole Role);
