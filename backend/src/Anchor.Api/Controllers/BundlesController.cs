using System.Text.RegularExpressions;
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
    private const int MaxNameLength = 128;
    private const int MaxValueLength = 512;

    // Loose hostname regex: letters/digits/hyphen labels separated by dots, with
    // an optional leading "*." for wildcard entries. Not a strict RFC 1035 check
    // (that'd reject many real production hostnames), just enough to catch
    // obviously-malformed input before it reaches the agent.
    private static readonly Regex DomainPattern = new(
        @"^(\*\.)?([a-z0-9]([a-z0-9-]*[a-z0-9])?)(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly AnchorDbContext _db;

    public BundlesController(AnchorDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<BundleSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<BundleSummary>>> List(
        [FromQuery] bool includeArchived,
        CancellationToken cancellationToken)
    {
        // Default: hide archived. Admins can opt in via ?includeArchived=true
        // to populate the catalogue editor; the picker (teachers) never sets
        // the flag and thus never sees soft-deleted entries.
        var query = _db.Bundles.AsNoTracking();
        if (!includeArchived)
            query = query.Where(b => !b.IsArchived);

        var bundles = await query
            .OrderBy(b => b.Name)
            .Select(b => new BundleSummary(b.Id, b.Name, b.Version, b.IsArchived))
            .ToListAsync(cancellationToken);

        return Ok(bundles);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(BundleDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BundleDetail>> Get(Guid id, CancellationToken cancellationToken)
    {
        // GET-by-id intentionally returns archived bundles too so historical
        // session views (#73 unblock-request logs, future audit) can resolve a
        // bundle name even after it's been soft-deleted.
        var bundle = await _db.Bundles
            .AsNoTracking()
            .Where(b => b.Id == id)
            .Select(b => new
            {
                b.Id,
                b.Name,
                b.Version,
                b.IsArchived,
                Entries = b.Entries
                    .OrderBy(e => e.Value)
                    .Select(e => new BundleEntrySummary(e.Kind, e.Value, e.MatchType))
                    .ToList(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (bundle is null)
            return NotFound();

        return Ok(new BundleDetail(bundle.Id, bundle.Name, bundle.Version, bundle.IsArchived, bundle.Entries));
    }

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(typeof(BundleDetail), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BundleDetail>> Create(
        [FromBody] WriteBundleRequest request,
        CancellationToken cancellationToken)
    {
        var (name, validationError) = NormaliseAndValidate(request);
        if (validationError is not null)
            return ValidationProblem(validationError);

        var nameTaken = await _db.Bundles.AsNoTracking()
            .AnyAsync(b => b.Name == name && !b.IsArchived, cancellationToken);
        if (nameTaken)
            return Conflict(new ProblemDetails { Title = "A bundle with that name already exists." });

        var bundle = new Bundle { Name = name!, Version = 1 };
        _db.Bundles.Add(bundle);
        foreach (var entry in request.Entries!)
        {
            _db.BundleEntries.Add(new BundleEntry
            {
                BundleId = bundle.Id,
                Kind = entry.Kind,
                Value = entry.Value.Trim(),
                MatchType = entry.MatchType,
            });
        }
        await _db.SaveChangesAsync(cancellationToken);

        var detail = await LoadDetailAsync(bundle.Id, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = bundle.Id }, detail);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(typeof(BundleDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BundleDetail>> Update(
        Guid id,
        [FromBody] WriteBundleRequest request,
        CancellationToken cancellationToken)
    {
        var (name, validationError) = NormaliseAndValidate(request);
        if (validationError is not null)
            return ValidationProblem(validationError);

        var bundle = await _db.Bundles
            .Include(b => b.Entries)
            .FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
        if (bundle is null)
            return NotFound();

        var nameTaken = await _db.Bundles.AsNoTracking()
            .AnyAsync(b => b.Id != id && b.Name == name && !b.IsArchived, cancellationToken);
        if (nameTaken)
            return Conflict(new ProblemDetails { Title = "A bundle with that name already exists." });

        bundle.Name = name!;
        // Bump the version on every save, even no-op ones. Picker + agent treat
        // the version as a cache-buster against the expanded allowlist
        // (#69/#70); skipping the bump on "unchanged" edits would silently
        // mask cases where the editor's draft equality check drifted from the
        // DB's notion of equality.
        bundle.Version += 1;
        bundle.IsArchived = false; // PUT implicitly un-archives — restoring an archived bundle is part of the editor flow.

        _db.BundleEntries.RemoveRange(bundle.Entries);
        foreach (var entry in request.Entries!)
        {
            _db.BundleEntries.Add(new BundleEntry
            {
                BundleId = bundle.Id,
                Kind = entry.Kind,
                Value = entry.Value.Trim(),
                MatchType = entry.MatchType,
            });
        }
        await _db.SaveChangesAsync(cancellationToken);

        var detail = await LoadDetailAsync(bundle.Id, cancellationToken);
        return Ok(detail);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Archive(Guid id, CancellationToken cancellationToken)
    {
        var bundle = await _db.Bundles.FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
        if (bundle is null)
            return NotFound();
        if (!bundle.IsArchived)
        {
            bundle.IsArchived = true;
            await _db.SaveChangesAsync(cancellationToken);
        }
        return NoContent();
    }

    private async Task<BundleDetail> LoadDetailAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await _db.Bundles.AsNoTracking()
            .Where(b => b.Id == id)
            .Select(b => new
            {
                b.Id,
                b.Name,
                b.Version,
                b.IsArchived,
                Entries = b.Entries
                    .OrderBy(e => e.Value)
                    .Select(e => new BundleEntrySummary(e.Kind, e.Value, e.MatchType))
                    .ToList(),
            })
            .FirstAsync(cancellationToken);
        return new BundleDetail(row.Id, row.Name, row.Version, row.IsArchived, row.Entries);
    }

    private static (string? Name, string? ValidationError) NormaliseAndValidate(WriteBundleRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return (null, "Name is required.");

        var name = request.Name.Trim();
        if (name.Length > MaxNameLength)
            return (null, $"Name must be at most {MaxNameLength} characters.");

        if (request.Entries is null || request.Entries.Count == 0)
            return (null, "At least one entry is required.");

        foreach (var entry in request.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Value))
                return (null, "Entry value cannot be empty.");

            var value = entry.Value.Trim();
            if (value.Length > MaxValueLength)
                return (null, $"Entry value must be at most {MaxValueLength} characters.");

            var error = ValidateEntryShape(entry.Kind, entry.MatchType, value);
            if (error is not null) return (null, error);
        }

        return (name, null);
    }

    private static string? ValidateEntryShape(BundleEntryKind kind, BundleEntryMatchType matchType, string value) => kind switch
    {
        BundleEntryKind.Domain => ValidateDomainEntry(matchType, value),
        BundleEntryKind.App => ValidateAppEntry(matchType, value),
        _ => $"Unknown entry kind '{kind}'.",
    };

    private static string? ValidateDomainEntry(BundleEntryMatchType matchType, string value)
    {
        if (matchType == BundleEntryMatchType.SignedPublisher)
            return "SignedPublisher is not valid for a Domain entry.";
        if (!DomainPattern.IsMatch(value))
            return $"'{value}' is not a valid domain.";
        return null;
    }

    private static string? ValidateAppEntry(BundleEntryMatchType matchType, string value)
    {
        if (matchType == BundleEntryMatchType.Wildcard || matchType == BundleEntryMatchType.Suffix)
            return $"{matchType} is not valid for an App entry.";
        if (matchType != BundleEntryMatchType.Exact) return null;

        // ProcessName: agent normalises by stripping ".exe" and path, but we
        // enforce the canonical form on input so the catalogue doesn't
        // accumulate inconsistent variants.
        if (value.Contains('\\', StringComparison.Ordinal) ||
            value.Contains('/', StringComparison.Ordinal))
            return $"Process name '{value}' must not include a path.";
        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return $"Process name '{value}' must not include the .exe suffix.";
        return null;
    }
}

public sealed record BundleSummary(Guid Id, string Name, int Version, bool IsArchived);

public sealed record BundleDetail(
    Guid Id,
    string Name,
    int Version,
    bool IsArchived,
    IReadOnlyList<BundleEntrySummary> Entries);

public sealed record BundleEntrySummary(
    BundleEntryKind Kind,
    string Value,
    BundleEntryMatchType MatchType);

public sealed record WriteBundleRequest(string Name, IReadOnlyList<WriteBundleEntry> Entries);

public sealed record WriteBundleEntry(
    BundleEntryKind Kind,
    string Value,
    BundleEntryMatchType MatchType);
