using Anchor.Domain.Classes;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Anchor.Api.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.Teacher)]
[Route("classes")]
public sealed class ClassesController : ControllerBase
{
    private readonly AnchorDbContext _db;
    private readonly IUserStore _users;

    public ClassesController(AnchorDbContext db, IUserStore users)
    {
        _db = db;
        _users = users;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ClassSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<ClassSummary>>> List(CancellationToken cancellationToken)
    {
        if (!User.TryGetEntraOid(out var entraOid))
            return Unauthorized();

        var caller = await _users.FindByEntraOidAsync(entraOid, cancellationToken);
        if (caller is null)
            return Unauthorized();

        var classes = await _db.ClassMemberships
            .AsNoTracking()
            .Where(m => m.UserId == caller.Id && m.Role == ClassMembershipRole.Teacher)
            .OrderBy(m => m.Class!.Name)
            .Select(m => new ClassSummary(m.Class!.Id, m.Class.Name, m.Class.SchoolYear))
            .ToListAsync(cancellationToken);

        return Ok(classes);
    }

    [HttpGet("{id:guid}/members")]
    [ProducesResponseType(typeof(ClassMembersResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ClassMembersResponse>> Members(Guid id, CancellationToken cancellationToken)
    {
        if (!User.TryGetEntraOid(out var entraOid))
            return Unauthorized();

        var caller = await _users.FindByEntraOidAsync(entraOid, cancellationToken);
        if (caller is null)
            return Unauthorized();

        var @class = await _db.Classes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (@class is null)
            return NotFound();

        var callerTeaches = await _db.ClassMemberships.AsNoTracking().AnyAsync(
            m => m.ClassId == id && m.UserId == caller.Id && m.Role == ClassMembershipRole.Teacher,
            cancellationToken);
        if (!callerTeaches)
            return Forbid();

        var members = await _db.ClassMemberships
            .AsNoTracking()
            .Where(m => m.ClassId == id)
            .OrderBy(m => m.User!.DisplayName)
            .Select(m => new ClassMemberSummary(m.User!.Id, m.User.DisplayName, m.User.Role, m.Role))
            .ToListAsync(cancellationToken);

        return Ok(new ClassMembersResponse(@class.Id, @class.Name, @class.SchoolYear, members));
    }
}

public sealed record ClassSummary(Guid Id, string Name, string SchoolYear);

public sealed record ClassMembersResponse(
    Guid Id,
    string Name,
    string SchoolYear,
    IReadOnlyList<ClassMemberSummary> Members);

public sealed record ClassMemberSummary(
    Guid UserId,
    string DisplayName,
    UserRole UserRole,
    ClassMembershipRole MembershipRole);
