using System.Text.Json.Serialization;
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

    /// Upper bound on the bulk-import result set. Graph filters can in theory
    /// match very large groups; the controller refuses to expand a class
    /// roster by more than this in a single shot.
    public const int MaxBulkImportRows = 500;

    public const int MaxSchoolTagLength = 64;
    public const int MaxClassCodeLength = 32;
    public const int MaxClassNameLength = 128;
    public const int MaxSchoolYearLength = 16;

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

    /// Lists the classes the caller teaches. Archived classes are left out
    /// (#395), so Home's class picker never offers one; the Classes page asks
    /// for them with <c>?includeArchived=true</c> to show and restore them.
    /// Each carries its session count, so the dashboard knows before a delete
    /// whether it takes sessions with it.
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ClassSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<ClassSummary>>> List(
        [FromQuery] bool includeArchived,
        CancellationToken cancellationToken)
    {
        if (!User.TryGetEntraOid(out var entraOid))
            return Unauthorized();

        var caller = await _users.FindByEntraOidAsync(entraOid, cancellationToken);
        if (caller is null)
            return Unauthorized();

        var taught = _db.ClassMemberships
            .AsNoTracking()
            .Where(m => m.UserId == caller.Id && m.Role == ClassMembershipRole.Teacher);
        if (!includeArchived)
            taught = taught.Where(m => !m.Class!.IsArchived);

        var classes = await taught
            .OrderBy(m => m.Class!.Name)
            .Select(m => new ClassSummary(
                m.Class!.Id,
                m.Class.Name,
                m.Class.SchoolYear,
                m.Class.SchoolTag,
                m.Class.ClassCode,
                m.Class.IsArchived,
                _db.Sessions.Count(s => s.ClassId == m.ClassId)))
            .ToListAsync(cancellationToken);

        return Ok(classes);
    }

    /// Creates a class and makes the caller its Teacher. Name + school year are
    /// required and unique together (matching the DB index); schoolTag /
    /// classCode are optional and scope the roster's Graph queries (#96).
    [HttpPost]
    [ProducesResponseType(typeof(ClassSummary), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ClassSummary>> Create(
        [FromBody] CreateClassRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            return BadRequest(new { error = "body is required" });

        var name = request.Name?.Trim();
        var schoolYear = request.SchoolYear?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { error = "name is required" });
        if (string.IsNullOrWhiteSpace(schoolYear))
            return BadRequest(new { error = "schoolYear is required" });
        if (name.Length > MaxClassNameLength)
            return BadRequest(new { error = $"name must be at most {MaxClassNameLength} characters" });
        if (schoolYear.Length > MaxSchoolYearLength)
            return BadRequest(new { error = $"schoolYear must be at most {MaxSchoolYearLength} characters" });
        if (request.SchoolTag is { Length: > MaxSchoolTagLength })
            return BadRequest(new { error = $"schoolTag must be at most {MaxSchoolTagLength} characters" });
        if (request.ClassCode is { Length: > MaxClassCodeLength })
            return BadRequest(new { error = $"classCode must be at most {MaxClassCodeLength} characters" });

        if (!User.TryGetEntraOid(out var entraOid))
            return Unauthorized();

        var caller = await _users.FindByEntraOidAsync(entraOid, cancellationToken);
        if (caller is null)
            return Unauthorized();

        // Mirror the unique (SchoolYear, Name) index with a friendly 409 rather
        // than letting SaveChanges throw a raw DbUpdateException. The index
        // covers archived classes too, so say when the clash is one (#395):
        // the teacher restores it rather than making a new one.
        var clash = await _db.Classes.AsNoTracking()
            .Where(c => c.SchoolYear == schoolYear && c.Name == name)
            .Select(c => new { c.IsArchived })
            .FirstOrDefaultAsync(cancellationToken);
        if (clash is not null)
        {
            return Conflict(new ClassNameConflict(
                clash.IsArchived
                    ? $"a class named '{name}' already exists for {schoolYear} and is archived; restore it instead"
                    : $"a class named '{name}' already exists for {schoolYear}",
                clash.IsArchived));
        }

        var @class = new Class
        {
            Name = name,
            SchoolYear = schoolYear,
            SchoolTag = NormalizeOrNull(request.SchoolTag),
            ClassCode = NormalizeOrNull(request.ClassCode),
        };
        _db.Classes.Add(@class);
        _db.ClassMemberships.Add(new ClassMembership
        {
            ClassId = @class.Id,
            UserId = caller.Id,
            Role = ClassMembershipRole.Teacher,
        });
        await _db.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(Members), new { id = @class.Id }, Summarize(@class, sessionCount: 0));
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
            auth.Class.SchoolTag,
            auth.Class.ClassCode,
            members));
    }

    /// Sets the school tag + class code on a class. Both fields are
    /// overwritten on every call; send null to clear. They scope every Graph
    /// query made on behalf of the class roster (#96).
    [HttpPatch("{id:guid}")]
    [ProducesResponseType(typeof(ClassSummary), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ClassSummary>> UpdateClass(
        Guid id,
        [FromBody] UpdateClassRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            return BadRequest(new { error = "body is required" });
        if (request.SchoolTag is { Length: > MaxSchoolTagLength })
            return BadRequest(new { error = $"schoolTag must be at most {MaxSchoolTagLength} characters" });
        if (request.ClassCode is { Length: > MaxClassCodeLength })
            return BadRequest(new { error = $"classCode must be at most {MaxClassCodeLength} characters" });

        var auth = await AuthorizeTeacherOfClassAsync(id, cancellationToken);
        if (auth.Result is not null) return auth.Result;

        // AsNoTracking() in the helper means we need to fetch a tracked copy.
        var tracked = await _db.Classes.FirstAsync(c => c.Id == id, cancellationToken);
        tracked.SchoolTag = NormalizeOrNull(request.SchoolTag);
        tracked.ClassCode = NormalizeOrNull(request.ClassCode);

        await _db.SaveChangesAsync(cancellationToken);

        return Ok(Summarize(tracked, await SessionCountAsync(id, cancellationToken)));
    }

    /// Archives a class the caller teaches (#395): it leaves the class lists
    /// and Home's class picker, and no session can start for it, while its
    /// roster and sessions stay. Idempotent.
    [HttpPost("{id:guid}/archive")]
    [ProducesResponseType(typeof(ClassSummary), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<ActionResult<ClassSummary>> Archive(Guid id, CancellationToken cancellationToken)
        => SetArchivedAsync(id, archived: true, cancellationToken);

    /// Restores an archived class the caller teaches (#395). Idempotent.
    [HttpPost("{id:guid}/unarchive")]
    [ProducesResponseType(typeof(ClassSummary), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<ActionResult<ClassSummary>> Unarchive(Guid id, CancellationToken cancellationToken)
        => SetArchivedAsync(id, archived: false, cancellationToken);

    private async Task<ActionResult<ClassSummary>> SetArchivedAsync(
        Guid id,
        bool archived,
        CancellationToken cancellationToken)
    {
        var auth = await AuthorizeTeacherOfClassAsync(id, cancellationToken);
        if (auth.Result is not null) return auth.Result;

        var tracked = await _db.Classes.FirstAsync(c => c.Id == id, cancellationToken);
        if (tracked.IsArchived != archived)
        {
            tracked.IsArchived = archived;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return Ok(Summarize(tracked, await SessionCountAsync(id, cancellationToken)));
    }

    /// Deletes a class the caller teaches, along with its memberships (which
    /// cascade). A class with sessions is refused with 409 unless the caller
    /// passes <c>?includeSessions=true</c> (#395), so a client that doesn't
    /// know the flag can never take sessions with it by accident. With the
    /// flag, the class's sessions go too, whoever started them, in the same
    /// transaction, and with them everything that cascades from a session
    /// (events, per-student activity counts, participants, unblock grants,
    /// session bundles). Refused with 409 while one of them is still running.
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteClass(
        Guid id,
        [FromQuery] bool includeSessions,
        CancellationToken cancellationToken)
    {
        var auth = await AuthorizeTeacherOfClassAsync(id, cancellationToken);
        if (auth.Result is not null) return auth.Result;

        var sessions = await _db.Sessions.Where(s => s.ClassId == id).ToListAsync(cancellationToken);
        if (sessions.Count > 0 && !includeSessions)
        {
            return Conflict(new
            {
                error = "class has session history; pass includeSessions=true to delete it with its sessions",
            });
        }
        if (sessions.Any(s => s.EndedAt is null))
        {
            return Conflict(new
            {
                error = "a session of this class is still running; end it before deleting the class",
            });
        }

        var tracked = await _db.Classes.FirstAsync(c => c.Id == id, cancellationToken);
        _db.Sessions.RemoveRange(sessions);
        _db.Classes.Remove(tracked);
        try
        {
            // One SaveChanges, so one transaction: the sessions go first
            // (Session -> Class is Restrict), then the class.
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // A session started for the class after the check above. Its row
            // still references the class, so the transaction rolled back and
            // nothing was deleted.
            _logger.LogWarning(ex, "Deleting class {ClassId} raced a new session", id);
            return Conflict(new
            {
                error = "a session of this class started meanwhile; end it before deleting the class",
            });
        }

        if (sessions.Count > 0)
        {
            _logger.LogInformation(
                "Class {ClassId} deleted with its {SessionCount} sessions", id, sessions.Count);
        }
        return NoContent();
    }

    private static string? NormalizeOrNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ClassSummary Summarize(Class @class, int sessionCount) => new(
        @class.Id,
        @class.Name,
        @class.SchoolYear,
        @class.SchoolTag,
        @class.ClassCode,
        @class.IsArchived,
        sessionCount);

    private Task<int> SessionCountAsync(Guid classId, CancellationToken cancellationToken)
        => _db.Sessions.CountAsync(s => s.ClassId == classId, cancellationToken);

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

        var schoolTag = auth.Class!.SchoolTag;
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

                if (schoolTag is not null
                    && !string.Equals(resolved.Company, schoolTag, StringComparison.OrdinalIgnoreCase))
                {
                    // Class is scoped to a school (#96); refuse rosters from
                    // any other school rather than silently mixing students.
                    results.Add(new ClassMembershipImportResult(
                        resolved.EntraOid,
                        null,
                        ClassMembershipImportStatus.WrongSchool,
                        $"user belongs to '{resolved.Company ?? "(none)"}', class is scoped to '{schoolTag}'",
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

    /// Populates the roster from Microsoft Graph in one shot by enumerating
    /// every user whose Entra <c>companyName</c> matches the class's
    /// <see cref="Class.SchoolTag"/> AND whose <c>department</c> matches
    /// <see cref="Class.ClassCode"/>. The class must have both fields set —
    /// otherwise we'd risk pulling every <c>3A</c> across the school group.
    [HttpPost("{id:guid}/members/bulk-import")]
    [ProducesResponseType(typeof(ImportClassMembersResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<ImportClassMembersResponse>> BulkImportFromDirectory(
        Guid id,
        CancellationToken cancellationToken)
    {
        var auth = await AuthorizeTeacherOfClassAsync(id, cancellationToken);
        if (auth.Result is not null) return auth.Result;

        var schoolTag = auth.Class!.SchoolTag;
        var classCode = auth.Class.ClassCode;
        if (string.IsNullOrWhiteSpace(schoolTag) || string.IsNullOrWhiteSpace(classCode))
        {
            return BadRequest(new
            {
                error = "class is missing schoolTag or classCode — set them with PATCH /classes/{id} first",
            });
        }

        IReadOnlyList<DirectoryUser> directoryUsers;
        try
        {
            directoryUsers = await _directory.ListByClassAsync(
                schoolTag, classCode, MaxBulkImportRows, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Roster bulk import directory listing failed");
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "directory listing unavailable" });
        }

        var results = new List<ClassMembershipImportResult>(directoryUsers.Count);
        foreach (var user in directoryUsers)
        {
            // Defensive: Graph should have filtered already, but verify so a
            // mis-attributed user (or a Graph filter regression) can't leak
            // across schools.
            if (!string.Equals(user.Company, schoolTag, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new ClassMembershipImportResult(
                    user.EntraOid,
                    null,
                    ClassMembershipImportStatus.WrongSchool,
                    $"user belongs to '{user.Company ?? "(none)"}', class is scoped to '{schoolTag}'",
                    user.Upn));
                continue;
            }

            var result = await UpsertMembershipAsync(
                id, user.EntraOid, user.DisplayName, ClassMembershipRole.Member, cancellationToken);
            results.Add(result with { Upn = user.Upn });
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

/// A class the caller teaches. <see cref="SessionCount"/> counts every session
/// of the class, whoever started it: the sessions a delete with
/// <c>?includeSessions=true</c> takes with it (#395).
public sealed record ClassSummary(
    Guid Id,
    string Name,
    string SchoolYear,
    string? SchoolTag = null,
    string? ClassCode = null,
    bool IsArchived = false,
    int SessionCount = 0);

/// The 409 for a new class whose name and school year are taken.
/// <see cref="Archived"/> says the class that has them is archived, so the
/// teacher restores it instead (#395).
public sealed record ClassNameConflict(string Error, bool Archived);

public sealed record ClassMembersResponse(
    Guid Id,
    string Name,
    string SchoolYear,
    string? SchoolTag,
    string? ClassCode,
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

public sealed record CreateClassRequest(
    string Name,
    string SchoolYear,
    string? SchoolTag = null,
    string? ClassCode = null);

public sealed record UpdateClassRequest(string? SchoolTag, string? ClassCode);

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

// By name on the wire: the dashboard reads "Added", "AlreadyMember", … and took
// the numeric form for an unknown status, so every row read as not found (#393).
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClassMembershipImportStatus
{
    Added,
    AlreadyMember,
    NotFoundInEntra,
    WrongSchool,
}
