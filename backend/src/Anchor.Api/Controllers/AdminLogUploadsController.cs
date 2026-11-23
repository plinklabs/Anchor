using System.IO.Compression;
using System.Text;
using System.Text.Json.Serialization;
using Anchor.Domain.Sessions;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Anchor.Api.Controllers;

/// <summary>
/// Admin-only access to the agents' log uploads of the test phase (#446), as
/// the dashboard's Admin → Logs page uses it (#566): list what is stored, and
/// download the uploads of one or more students of one session. A download
/// states its purpose (one of the two the privacy policy allows), a reason and,
/// for a fraud suspicion, who in the school's leadership asked; the backend
/// writes a <see cref="LogAccess"/> record per student before any log content
/// leaves it, in the same transaction. No route returns log content without
/// one. Uploads are deleted <c>LogUpload:RetentionDays</c> after they arrived;
/// the records live on (<see cref="AdminLogAccessController"/>).
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.Admin)]
[Route("admin/log-uploads")]
public sealed class AdminLogUploadsController : ControllerBase
{
    public const string ZipContentType = "application/zip";

    private readonly AnchorDbContext _db;
    private readonly IUserStore _users;
    private readonly TimeProvider _clock;

    public AdminLogUploadsController(AnchorDbContext db, IUserStore users, TimeProvider clock)
    {
        _db = db;
        _users = users;
        _clock = clock;
    }

    /// <summary>
    /// The stored uploads, newest first, optionally of one session and/or one
    /// student. Metadata only; download them with <c>POST download</c>.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<LogUploadSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<LogUploadSummary>>> List(
        [FromQuery] Guid? sessionId,
        [FromQuery] Guid? userId,
        CancellationToken cancellationToken)
    {
        var uploads = _db.SessionLogUploads.AsNoTracking();
        if (sessionId is { } session)
            uploads = uploads.Where(u => u.SessionId == session);
        if (userId is { } user)
            uploads = uploads.Where(u => u.UserId == user);

        var rows = await uploads
            .Join(_db.Users.AsNoTracking(), u => u.UserId, user => user.Id, (u, user) => new { u, user.DisplayName })
            .Join(_db.Sessions.AsNoTracking(), x => x.u.SessionId, s => s.Id, (x, s) => new { x.u, x.DisplayName, s.StartedAt, s.ClassId })
            .Join(_db.Classes.AsNoTracking(), x => x.ClassId, c => c.Id, (x, c) => new LogUploadSummary(
                x.u.SessionId,
                x.u.UserId,
                x.DisplayName,
                c.Name,
                x.StartedAt,
                x.u.WindowFrom,
                x.u.WindowTo,
                x.u.IsComplete,
                x.u.UploadedAt,
                x.u.AgentVersion,
                x.u.ContentLength))
            .ToListAsync(cancellationToken);

        // SQLite (dev + tests) can't ORDER BY a DateTimeOffset server side; the
        // table holds two weeks of uploads at most.
        return Ok(rows.OrderByDescending(r => r.UploadedAt).ToList());
    }

    /// <summary>
    /// The uploads of <see cref="DownloadLogUploadsRequest.UserIds"/> in one
    /// session, as a file for offline analysis: one student's as the gzipped
    /// log text the agent sent (<c>.txt.gz</c>), several as one <c>.zip</c> of
    /// those. Refused (400) without a purpose and a reason, and for a fraud
    /// suspicion without who asked; 404 when a student has no upload of the
    /// session. Before it returns anything, it records the download, one
    /// <see cref="LogAccess"/> per student, in the transaction that reads the
    /// logs: a download whose record can't be written returns no log.
    /// </summary>
    [HttpPost("download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(
        [FromBody] DownloadLogUploadsRequest request,
        CancellationToken cancellationToken)
    {
        if (!User.TryGetEntraOid(out var entraOid)
            || await _users.FindByEntraOidAsync(entraOid, cancellationToken) is not { } admin)
            return Unauthorized();

        var userIds = (request.UserIds ?? []).Distinct().ToList();
        if (userIds.Count == 0)
            return ValidationProblem("userIds: pick at least one student.");
        if (!Enum.IsDefined(request.Purpose))
            return ValidationProblem("purpose must be TechnicalDiagnosis or FraudSuspicion.");
        var reason = request.Reason?.Trim() ?? "";
        if (reason.Length < LogAccess.MinReasonLength)
            return ValidationProblem($"reason is required: at least {LogAccess.MinReasonLength} characters.");
        if (reason.Length > LogAccess.MaxReasonLength)
            return ValidationProblem($"reason is at most {LogAccess.MaxReasonLength} characters.");
        var requestedBy = string.IsNullOrWhiteSpace(request.RequestedBy) ? null : request.RequestedBy.Trim();
        if (request.Purpose == LogAccessPurpose.FraudSuspicion && requestedBy is null)
            return ValidationProblem("requestedBy is required for a fraud suspicion: who in the school's leadership asked.");
        if (requestedBy is { Length: > LogAccess.MaxRequestedByLength })
            return ValidationProblem($"requestedBy is at most {LogAccess.MaxRequestedByLength} characters.");

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var uploads = await _db.SessionLogUploads.AsNoTracking()
            .Where(u => u.SessionId == request.SessionId && userIds.Contains(u.UserId))
            .Join(_db.Users.AsNoTracking(), u => u.UserId, user => user.Id, (u, user) => new { u.UserId, user.DisplayName, u.Content, u.SessionId })
            .Join(_db.Sessions.AsNoTracking(), x => x.SessionId, s => s.Id, (x, s) => new { x.UserId, x.DisplayName, x.Content, s.StartedAt, s.ClassId })
            .Join(_db.Classes.AsNoTracking(), x => x.ClassId, c => c.Id, (x, c) => new { x.UserId, x.DisplayName, x.Content, x.StartedAt, ClassName = c.Name })
            .ToListAsync(cancellationToken);
        var missing = userIds.Except(uploads.Select(u => u.UserId)).ToList();
        if (missing.Count > 0)
            return NotFound(new { error = "no upload of this session for these students", userIds = missing });

        var now = _clock.GetUtcNow();
        foreach (var upload in uploads)
        {
            _db.LogAccesses.Add(new LogAccess
            {
                AccessedAt = now,
                AdminUserId = admin.Id,
                AdminDisplayName = admin.DisplayName,
                SessionId = request.SessionId,
                SessionStartedAt = upload.StartedAt,
                ClassName = upload.ClassName,
                StudentUserId = upload.UserId,
                StudentDisplayName = upload.DisplayName,
                Purpose = request.Purpose,
                Reason = reason,
                RequestedBy = requestedBy,
            });
        }
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (uploads.Count == 1)
        {
            var single = uploads[0];
            return File(single.Content, LogUploadsController.GzipContentType, SingleFileName(request.SessionId, single.UserId));
        }

        // The uploads are gzipped already: stored in the zip as they are.
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var upload in uploads.OrderBy(u => u.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                var entry = zip.CreateEntry($"{SafeName(upload.DisplayName)}-{upload.UserId}.txt.gz", CompressionLevel.NoCompression);
                await using var stream = entry.Open();
                await stream.WriteAsync(upload.Content, cancellationToken);
            }
        }
        return File(buffer.ToArray(), ZipContentType, ZipFileName(request.SessionId));
    }

    public static string SingleFileName(Guid sessionId, Guid userId) => $"anchor-agent-log-{sessionId}-{userId}.txt.gz";

    public static string ZipFileName(Guid sessionId) => $"anchor-agent-logs-{sessionId}.zip";

    /// <summary>A display name as a part of a file name: letters, digits, spaces and dashes only.</summary>
    private static string SafeName(string name)
    {
        var safe = new StringBuilder(name.Length);
        foreach (var c in name.Trim())
            safe.Append(char.IsLetterOrDigit(c) || c is ' ' or '-' ? c : '_');
        return safe.Length == 0 ? "student" : safe.ToString();
    }
}

public sealed record LogUploadSummary(
    Guid SessionId,
    Guid UserId,
    string DisplayName,
    string ClassName,
    DateTimeOffset SessionStartedAt,
    DateTimeOffset WindowFrom,
    DateTimeOffset WindowTo,
    bool IsComplete,
    DateTimeOffset UploadedAt,
    string? AgentVersion,
    int SizeBytes);

/// <summary>
/// What an admin downloads and why (#566): the students' uploads of one
/// session, the purpose, a reason in their own words and, for a fraud
/// suspicion, who in the school's leadership asked. A request without a
/// purpose or a reason is refused.
/// </summary>
public sealed record DownloadLogUploadsRequest(
    Guid SessionId,
    IReadOnlyList<Guid> UserIds,
    [property: JsonRequired] LogAccessPurpose Purpose,
    string Reason,
    string? RequestedBy);
