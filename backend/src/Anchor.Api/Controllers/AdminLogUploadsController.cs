using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Anchor.Api.Controllers;

/// <summary>
/// Admin-only access to the agents' log uploads of the test phase (#446): list
/// what is stored, and download one session's upload of one student, as the
/// gzipped text the agent sent. No dashboard UI uses these yet. Uploads are
/// deleted <c>LogUpload:RetentionDays</c> after they arrived.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.Admin)]
[Route("admin/log-uploads")]
public sealed class AdminLogUploadsController : ControllerBase
{
    private readonly AnchorDbContext _db;

    public AdminLogUploadsController(AnchorDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// The stored uploads, newest first, optionally of one session and/or one
    /// student. Metadata only; download one with
    /// <c>GET admin/log-uploads/{sessionId}/{userId}</c>.
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
            .Join(_db.Users.AsNoTracking(),
                u => u.UserId,
                user => user.Id,
                (u, user) => new LogUploadSummary(
                    u.SessionId,
                    u.UserId,
                    user.DisplayName,
                    u.WindowFrom,
                    u.WindowTo,
                    u.IsComplete,
                    u.UploadedAt,
                    u.AgentVersion,
                    u.ContentLength))
            .ToListAsync(cancellationToken);

        // SQLite (dev + tests) can't ORDER BY a DateTimeOffset server side; the
        // table holds two weeks of uploads at most.
        return Ok(rows.OrderByDescending(r => r.UploadedAt).ToList());
    }

    /// <summary>One session's upload of one student: the gzipped log text.</summary>
    [HttpGet("{sessionId:guid}/{userId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(Guid sessionId, Guid userId, CancellationToken cancellationToken)
    {
        var content = await _db.SessionLogUploads.AsNoTracking()
            .Where(u => u.SessionId == sessionId && u.UserId == userId)
            .Select(u => u.Content)
            .FirstOrDefaultAsync(cancellationToken);
        if (content is null)
            return NotFound();

        return File(content, LogUploadsController.GzipContentType, $"anchor-agent-log-{sessionId}-{userId}.txt.gz");
    }
}

public sealed record LogUploadSummary(
    Guid SessionId,
    Guid UserId,
    string DisplayName,
    DateTimeOffset WindowFrom,
    DateTimeOffset WindowTo,
    bool IsComplete,
    DateTimeOffset UploadedAt,
    string? AgentVersion,
    int SizeBytes);
