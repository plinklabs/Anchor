using Anchor.Api.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Anchor.Api.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.Teacher)]
[Route("users")]
public sealed class UsersController : ControllerBase
{
    public const int MinQueryLength = 2;
    public const int MaxQueryLength = 64;
    public const int DefaultTop = 10;
    public const int MaxTop = 25;

    private readonly IUserDirectorySearch _search;
    private readonly ILogger<UsersController> _logger;

    public UsersController(IUserDirectorySearch search, ILogger<UsersController> logger)
    {
        _search = search;
        _logger = logger;
    }

    [HttpGet("search")]
    [ProducesResponseType(typeof(IReadOnlyList<DirectoryUserResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<IReadOnlyList<DirectoryUserResponse>>> Search(
        [FromQuery(Name = "q")] string? q,
        [FromQuery] int? top,
        CancellationToken cancellationToken)
    {
        var trimmed = (q ?? string.Empty).Trim();
        if (trimmed.Length < MinQueryLength)
            return BadRequest(new { error = $"q must be at least {MinQueryLength} characters" });
        if (trimmed.Length > MaxQueryLength)
            return BadRequest(new { error = $"q must be at most {MaxQueryLength} characters" });

        var clampedTop = Math.Clamp(top ?? DefaultTop, 1, MaxTop);

        try
        {
            var results = await _search.SearchAsync(trimmed, clampedTop, cancellationToken);
            return Ok(results
                .Select(r => new DirectoryUserResponse(r.EntraOid, r.DisplayName, r.Upn))
                .ToList());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Most common cause is OBO failure (missing client secret in
            // config, or the teacher hasn't consented to User.ReadBasic.All).
            // Surface a generic 502 — the SPA shows "directory unavailable"
            // and the server log retains the detail for ops.
            _logger.LogWarning(ex, "Directory search failed");
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "directory search unavailable" });
        }
    }
}

public sealed record DirectoryUserResponse(Guid EntraOid, string DisplayName, string? Upn);
