using Anchor.Api.Users;
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
    public const int MaxImportRows = 200;

    private readonly AnchorDbContext _db;
    private readonly IUserStore _users;
    private readonly IUserDirectorySearch _directory;
    private readonly ILogger<ClassesController> _logger;

    public ClassesController(
        AnchorDbContext db,
        IUserStore users,
        IUserDirectorySearch directory,
        ILogger<ClassesController> logger)
    {
        _db = db;
        _users = users;
        _directory = directory;
        _logger = logger;
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
        var auth = await AuthorizeTeacherOfClassAsync(id, cancellationToken);
        if (auth.Result is not null) return auth.Result;

        var members = await _db.ClassMemberships
            .AsNoTracking()
            .Where(m => m.ClassId == id)
            .OrderBy(m => m.User!.DisplayName)
            .Select(m => new ClassMemberSummary(
                m.User!.Id,
                m.User.EntraOid,
                m.User.DisplayName,
                m.User.Role,
                m.Role,
                m.JoinedAt))
            .ToListAsync(cancellationToken);

        return Ok(new ClassMembersResponse(
            auth.Class!.Id,
            auth.Class.Name,
            auth.Class.SchoolYear,
            members));
    }

    [HttpPost("{id:guid}/members")]
    [ProducesResponseType(typeof(ClassMembershipImportResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ClassMembershipImportResult), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ClassMembershipImportResult>> AddMember(
        Guid id,
        [FromBody] AddClassMemberRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || request.EntraOid == Guid.Empty)
            return BadRequest(new { error = "entraOid is required" });

        var auth = await AuthorizeTeacherOfClassAsync(id, cancellationToken);
        if (auth.Result is not null) return auth.Result;

        var role = request.Role ?? ClassMembershipRole.Member;
        var result = await UpsertMembershipAsync(id, request.EntraOid, request.DisplayName, role, cancellationToken);

        return result.Status switch
        {
            ClassMembershipImportStatus.Added => StatusCode(StatusCodes.Status201Created, result),
            ClassMembershipImportStatus.AlreadyMember => Ok(result),
            // NotFoundInEntra cannot happen for the single-add path because we
            // always create a placeholder when DisplayName is supplied; if it's
            // not supplied and the user doesn't exist, surface a 400.
            ClassMembershipImportStatus.NotFoundInEntra => BadRequest(new
            {
                error = "user is unknown — supply displayName to create a placeholder",
            }),
            _ => StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    [HttpDelete("{id:guid}/members/{userId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveMember(Guid id, Guid userId, CancellationToken cancellationToken)
    {
        var auth = await AuthorizeTeacherOfClassAsync(id, cancellationToken);
        if (auth.Result is not null) return auth.Result;

        var membership = await _db.ClassMemberships
            .FirstOrDefaultAsync(m => m.ClassId == id && m.UserId == userId, cancellationToken);
        if (membership is null)
            return NotFound();

        _db.ClassMemberships.Remove(membership);
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("{id:guid}/members/import")]
    [ProducesResponseType(typeof(ImportClassMembersResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ImportClassMembersResponse>> ImportMembers(
        Guid id,
        [FromBody] ImportClassMembersRequest request,
        CancellationToken cancellationToken)
    {
        if (request?.Rows is null)
            return BadRequest(new { error = "rows is required" });
        if (request.Rows.Count > MaxImportRows)
            return BadRequest(new { error = $"max {MaxImportRows} rows per request" });

        var auth = await AuthorizeTeacherOfClassAsync(id, cancellationToken);
        if (auth.Result is not null) return auth.Result;

        var results = new List<ClassMembershipImportResult>(request.Rows.Count);
        try
        {
            foreach (var row in request.Rows)
            {
                var upn = row?.Upn?.Trim();
                if (string.IsNullOrEmpty(upn))
                {
                    results.Add(new ClassMembershipImportResult(
                        null,
                        null,
                        ClassMembershipImportStatus.NotFoundInEntra,
                        "missing upn",
                        upn));
                    continue;
                }

                var resolved = await _directory.ResolveByUpnAsync(upn, cancellationToken);
                if (resolved is null)
                {
                    results.Add(new ClassMembershipImportResult(
                        null,
                        null,
                        ClassMembershipImportStatus.NotFoundInEntra,
                        "could not resolve UPN in directory",
                        upn));
                    continue;
                }

                var role = row!.Role ?? ClassMembershipRole.Member;
                var result = await UpsertMembershipAsync(
                    id, resolved.EntraOid, resolved.DisplayName, role, cancellationToken);
                results.Add(result with { Upn = upn });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // ResolveByUpnAsync only throws for systemic directory failures
            // (consent/secret missing, throttling, outage) — same surface as
            // UsersController. Per-row "not found" returns null and is handled
            // above, so reaching here means we can't resolve anything.
            _logger.LogWarning(ex, "Roster import directory lookup failed");
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "directory lookup unavailable" });
        }

        return Ok(new ImportClassMembersResponse(results));
    }

    private async Task<(ActionResult? Result, Class? Class)> AuthorizeTeacherOfClassAsync(
        Guid classId,
        CancellationToken cancellationToken)
    {
        if (!User.TryGetEntraOid(out var entraOid))
            return (Unauthorized(), null);

        var caller = await _users.FindByEntraOidAsync(entraOid, cancellationToken);
        if (caller is null)
            return (Unauthorized(), null);

        var @class = await _db.Classes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == classId, cancellationToken);
        if (@class is null)
            return (NotFound(), null);

        var callerTeaches = await _db.ClassMemberships.AsNoTracking().AnyAsync(
            m => m.ClassId == classId && m.UserId == caller.Id && m.Role == ClassMembershipRole.Teacher,
            cancellationToken);
        if (!callerTeaches)
            return (Forbid(), null);

        return (null, @class);
    }

    private async Task<ClassMembershipImportResult> UpsertMembershipAsync(
        Guid classId,
        Guid entraOid,
        string? displayName,
        ClassMembershipRole role,
        CancellationToken cancellationToken)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.EntraOid == entraOid, cancellationToken);
        if (user is null)
        {
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return new ClassMembershipImportResult(
                    entraOid,
                    null,
                    ClassMembershipImportStatus.NotFoundInEntra,
                    "user not in directory and no displayName supplied");
            }

            // Placeholder user — role defaults to Student. When they actually
            // sign in, MeController.UpsertAsync overwrites DisplayName + Role
            // with whatever Entra returns. This mirrors the issue spec.
            user = new User
            {
                EntraOid = entraOid,
                DisplayName = displayName,
                Role = UserRole.Student,
            };
            _db.Users.Add(user);
            await _db.SaveChangesAsync(cancellationToken);
        }

        var existing = await _db.ClassMemberships
            .FirstOrDefaultAsync(m => m.ClassId == classId && m.UserId == user.Id, cancellationToken);
        if (existing is not null)
        {
            return new ClassMembershipImportResult(
                entraOid,
                user.Id,
                ClassMembershipImportStatus.AlreadyMember,
                null);
        }

        _db.ClassMemberships.Add(new ClassMembership
        {
            ClassId = classId,
            UserId = user.Id,
            Role = role,
        });
        await _db.SaveChangesAsync(cancellationToken);

        return new ClassMembershipImportResult(
            entraOid,
            user.Id,
            ClassMembershipImportStatus.Added,
            null);
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
    Guid EntraOid,
    string DisplayName,
    UserRole UserRole,
    ClassMembershipRole MembershipRole,
    DateTimeOffset JoinedAt);

public sealed record AddClassMemberRequest(
    Guid EntraOid,
    string? DisplayName,
    ClassMembershipRole? Role);

public sealed record ImportClassMembersRequest(IReadOnlyList<ImportClassMemberRow> Rows);

public sealed record ImportClassMemberRow(
    string Upn,
    ClassMembershipRole? Role);

public sealed record ImportClassMembersResponse(IReadOnlyList<ClassMembershipImportResult> Results);

public sealed record ClassMembershipImportResult(
    Guid? EntraOid,
    Guid? UserId,
    ClassMembershipImportStatus Status,
    string? Detail,
    string? Upn = null);

public enum ClassMembershipImportStatus
{
    Added,
    AlreadyMember,
    NotFoundInEntra,
}
