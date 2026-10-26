using Anchor.Api.Diagnostics;
using Anchor.Domain.Sessions;
using Anchor.Domain.Users;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Anchor.Api.Controllers;

/// <summary>
/// The agent's side of the test phase's log uploads (#446). On every connect
/// the agent asks which session windows still need an upload from its student,
/// cuts its log lines inside each window and uploads them, gzipped. The backend
/// decides the windows: every session the student was on the roster of (or
/// joined by code) that started within <see cref="LogUploadOptions.LookbackDays"/>,
/// so a session whose agent never joined is asked for as well. Off by default
/// (<see cref="LogUploadOptions.Enabled"/>): while off, no windows are reported
/// and nothing is taken. Only admins can download an upload
/// (<see cref="AdminLogUploadsController"/>).
/// </summary>
[ApiController]
[Authorize]
[Route("log-uploads")]
public sealed class LogUploadsController : ControllerBase
{
    public const string GzipContentType = "application/gzip";

    private readonly AnchorDbContext _db;
    private readonly IUserStore _users;
    private readonly TimeProvider _clock;
    private readonly IOptionsMonitor<LogUploadOptions> _options;

    public LogUploadsController(
        AnchorDbContext db,
        IUserStore users,
        TimeProvider clock,
        IOptionsMonitor<LogUploadOptions> options)
    {
        _db = db;
        _users = users;
        _clock = clock;
        _options = options;
    }

    /// <summary>
    /// The caller's session windows the backend still needs an upload of,
    /// oldest first, and the largest upload it takes per window. A window stays
    /// pending until the backend holds a complete upload of it: one cut once the
    /// session had ended and the window had closed. An open window (a running
    /// session) is reported up to now, so an agent that connects mid-session
    /// uploads what it has so far, and again once the window has closed.
    /// </summary>
    [HttpGet("pending")]
    [ProducesResponseType(typeof(PendingLogUploadsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PendingLogUploadsResponse>> Pending(CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(cancellationToken);
        if (caller is null)
            return Unauthorized();

        var options = _options.CurrentValue;
        if (!options.Enabled)
            return Ok(new PendingLogUploadsResponse(options.EffectiveMaxUploadBytes, Array.Empty<PendingLogWindow>()));

        var sessions = await _db.SessionParticipants.AsNoTracking()
            .Where(p => p.UserId == caller.Id)
            .Join(_db.Sessions.AsNoTracking(),
                p => p.SessionId,
                s => s.Id,
                (p, s) => new { s.Id, s.StartedAt, s.EndedAt })
            .ToListAsync(cancellationToken);
        var complete = (await _db.SessionLogUploads.AsNoTracking()
                .Where(u => u.UserId == caller.Id && u.IsComplete)
                .Select(u => u.SessionId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        // SQLite (dev + tests) can't compare DateTimeOffset values server side,
        // so the lookback and the order are applied after materialisation. A
        // student's participant rows are bounded (a few sessions a school day).
        var now = _clock.GetUtcNow();
        var windows = sessions
            .Where(s => s.StartedAt >= now - options.Lookback && !complete.Contains(s.Id))
            .OrderBy(s => s.StartedAt)
            .Select(s =>
            {
                var window = LogUploadWindow.For(s.StartedAt, s.EndedAt, now, options);
                return new PendingLogWindow(s.Id, window.From, window.To);
            })
            .ToList();

        return Ok(new PendingLogUploadsResponse(options.EffectiveMaxUploadBytes, windows));
    }

    /// <summary>
    /// Store the caller's log lines from a session's window: the gzipped text as
    /// the request body, <paramref name="to"/> the window end the agent cut its
    /// lines to (as <c>GET pending</c> reported it). Replaces an earlier upload
    /// of the same window, which held part of the same lines; refused once a
    /// complete one is stored. The upload is always the caller's own: a student
    /// can only upload for a session they were on the roster of.
    /// </summary>
    [HttpPost("{sessionId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> Upload(
        Guid sessionId,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? agentVersion,
        CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(cancellationToken);
        if (caller is null)
            return Unauthorized();

        var options = _options.CurrentValue;
        if (!options.Enabled)
            return NotFound(new { error = "log uploads are off" });

        var session = await _db.SessionParticipants.AsNoTracking()
            .Where(p => p.SessionId == sessionId && p.UserId == caller.Id)
            .Join(_db.Sessions.AsNoTracking(),
                p => p.SessionId,
                s => s.Id,
                (p, s) => new { s.StartedAt, s.EndedAt })
            .FirstOrDefaultAsync(cancellationToken);
        var now = _clock.GetUtcNow();
        if (session is null || session.StartedAt < now - options.Lookback)
            return NotFound();

        var window = LogUploadWindow.For(session.StartedAt, session.EndedAt, now, options);
        if (to is not { } cutTo)
            return ValidationProblem("to (the window end the lines were cut to) is required.");
        if (cutTo < window.From || cutTo > window.To)
            return ValidationProblem("to must lie within the session's window.");

        var existing = await _db.SessionLogUploads
            .FirstOrDefaultAsync(u => u.SessionId == sessionId && u.UserId == caller.Id, cancellationToken);
        if (existing is { IsComplete: true })
            return Conflict(new { error = "this window has already been uploaded" });

        if (agentVersion is { Length: > 64 })
            return ValidationProblem("agentVersion is at most 64 characters.");

        var maxBytes = options.EffectiveMaxUploadBytes;
        if (Request.ContentLength > maxBytes)
            return TooLarge(maxBytes);
        if (HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeLimit)
            sizeLimit.MaxRequestBodySize = maxBytes + 1L;

        var content = await ReadBodyAsync(maxBytes + 1, cancellationToken);
        if (content.Length > maxBytes)
            return TooLarge(maxBytes);
        // Stored as sent; only checked to be gzip, never unpacked here.
        if (content.Length < 2 || content[0] != 0x1f || content[1] != 0x8b)
            return ValidationProblem("The body must be the gzipped log text.");

        var isComplete = window.IsClosed && cutTo >= window.To;
        if (existing is null)
        {
            _db.SessionLogUploads.Add(new SessionLogUpload
            {
                SessionId = sessionId,
                UserId = caller.Id,
                WindowFrom = window.From,
                WindowTo = cutTo,
                IsComplete = isComplete,
                UploadedAt = now,
                AgentVersion = agentVersion,
                ContentLength = content.Length,
                Content = content,
            });
        }
        else
        {
            existing.WindowFrom = window.From;
            existing.WindowTo = cutTo;
            existing.IsComplete = isComplete;
            existing.UploadedAt = now;
            existing.AgentVersion = agentVersion;
            existing.ContentLength = content.Length;
            existing.Content = content;
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two uploads of the same window at once: the first one stored wins.
            return Conflict(new { error = "another upload of this window was stored at the same time" });
        }

        return NoContent();
    }

    private async Task<User?> CallerAsync(CancellationToken cancellationToken) =>
        User.TryGetEntraOid(out var entraOid)
            ? await _users.FindByEntraOidAsync(entraOid, cancellationToken)
            : null;

    private ObjectResult TooLarge(int maxBytes) =>
        StatusCode(StatusCodes.Status413PayloadTooLarge,
            new { error = $"an upload is at most {maxBytes} bytes", maxBytes });

    private async Task<byte[]> ReadBodyAsync(int limit, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while (buffer.Length < limit
               && (read = await Request.Body.ReadAsync(
                   chunk.AsMemory(0, (int)Math.Min(chunk.Length, limit - buffer.Length)), cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}

/// <summary>
/// The windows the backend still needs an upload of, and the largest upload it
/// takes per window (gzipped bytes).
/// </summary>
public sealed record PendingLogUploadsResponse(int MaxBytes, IReadOnlyList<PendingLogWindow> Windows);

/// <summary>
/// One session's window: the agent uploads its log lines from
/// <paramref name="From"/> to <paramref name="To"/>, and nothing outside it.
/// </summary>
public sealed record PendingLogWindow(Guid SessionId, DateTimeOffset From, DateTimeOffset To);
