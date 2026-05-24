using Anchor.Domain.Bundles;
using Anchor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Anchor.Api.Controllers;

[ApiController]
[Authorize]
[Route("bundles")]
public sealed class BundlesController : ControllerBase
{
    private readonly AnchorDbContext _db;

    public BundlesController(AnchorDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<BundleSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<BundleSummary>>> List(CancellationToken cancellationToken)
    {
        var bundles = await _db.Bundles
            .AsNoTracking()
            .OrderBy(b => b.Name)
            .Select(b => new BundleSummary(b.Id, b.Name, b.Version))
            .ToListAsync(cancellationToken);

        return Ok(bundles);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(BundleDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BundleDetail>> Get(Guid id, CancellationToken cancellationToken)
    {
        var bundle = await _db.Bundles
            .AsNoTracking()
            .Where(b => b.Id == id)
            .Select(b => new
            {
                b.Id,
                b.Name,
                b.Version,
                Entries = b.Entries
                    .OrderBy(e => e.Value)
                    .Select(e => new BundleEntrySummary(e.Kind, e.Value, e.MatchType))
                    .ToList(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (bundle is null)
            return NotFound();

        return Ok(new BundleDetail(bundle.Id, bundle.Name, bundle.Version, bundle.Entries));
    }
}

public sealed record BundleSummary(Guid Id, string Name, int Version);

public sealed record BundleDetail(
    Guid Id,
    string Name,
    int Version,
    IReadOnlyList<BundleEntrySummary> Entries);

public sealed record BundleEntrySummary(
    BundleEntryKind Kind,
    string Value,
    BundleEntryMatchType MatchType);
