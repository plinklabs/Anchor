using Anchor.Api.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Anchor.Api.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.Teacher)]
[Route("directory")]
public sealed class DirectoryController : ControllerBase
{
    private readonly IUserDirectorySearch _search;
    private readonly ILogger<DirectoryController> _logger;

    public DirectoryController(IUserDirectorySearch search, ILogger<DirectoryController> logger)
    {
        _search = search;
        _logger = logger;
    }

    /// Returns the distinct school tags (Entra <c>companyName</c> values)
    /// available in the directory, so the roster screen can offer a school
    /// selector before scoping by class code (#96).
    [HttpGet("schools")]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<IReadOnlyList<string>>> Schools(CancellationToken cancellationToken)
    {
        try
        {
            var schools = await _search.ListCompaniesAsync(cancellationToken);
            return Ok(schools);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Listing schools from directory failed");
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "directory unavailable" });
        }
    }
}
