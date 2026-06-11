using Anchor.Api.Realtime;
using Anchor.Domain.Bundles;
using Anchor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Anchor.Api.Sessions;

/// <summary>
/// Turns the set of selected bundle ids into the concrete wire payload the
/// agent and extension will enforce against: bundles merged with the baseline
/// allowlist (apps + domains) (#70).
///
/// Dedupes on (match-kind, lowercase value) within each list so a bundle
/// that repeats a baseline entry doesn't double-list it.
/// </summary>
public interface ISessionAllowlistExpander
{
    Task<ExpandedAllowlist> ExpandAsync(IReadOnlyCollection<Guid> bundleIds, CancellationToken cancellationToken = default);

    Task<ExpandedAllowlist> ExpandForSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

public sealed record ExpandedAllowlist(
    IReadOnlyList<AllowedAppDto> Apps,
    IReadOnlyList<AllowedDomainDto> Domains);

public sealed class SessionAllowlistExpander : ISessionAllowlistExpander
{
    private readonly AnchorDbContext _db;
    private readonly bool _includeDevelopmentCarveouts;

    public SessionAllowlistExpander(AnchorDbContext db, IHostEnvironment environment)
    {
        _db = db;
        // Dev-only carve-outs (#125): localhost + VS Code. Computed once here so
        // a Release build never merges them, no matter which expansion path runs.
        _includeDevelopmentCarveouts = environment.IsDevelopment();
    }

    public async Task<ExpandedAllowlist> ExpandAsync(IReadOnlyCollection<Guid> bundleIds, CancellationToken cancellationToken = default)
    {
        var entries = bundleIds.Count == 0
            ? Array.Empty<BundleEntry>()
            : await _db.BundleEntries
                .AsNoTracking()
                .Where(e => bundleIds.Contains(e.BundleId))
                .ToArrayAsync(cancellationToken);

        return Merge(entries);
    }

    public async Task<ExpandedAllowlist> ExpandForSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var entries = await _db.SessionBundles
            .AsNoTracking()
            .Where(sb => sb.SessionId == sessionId)
            .SelectMany(sb => _db.BundleEntries.Where(e => e.BundleId == sb.BundleId))
            .ToArrayAsync(cancellationToken);

        return Merge(entries);
    }

    private ExpandedAllowlist Merge(IReadOnlyCollection<BundleEntry> entries)
    {
        var apps = new List<AllowedAppDto>(SessionAllowlist.BaselineApps);
        var domains = new List<AllowedDomainDto>(SessionAllowlist.BaselineDomains);

        if (_includeDevelopmentCarveouts)
        {
            apps.AddRange(SessionAllowlist.DevelopmentApps);
            domains.AddRange(SessionAllowlist.DevelopmentDomains);
        }

        foreach (var entry in entries)
        {
            switch (entry.Kind)
            {
                case BundleEntryKind.App:
                    var appKind = MapAppMatchKind(entry.MatchType);
                    if (appKind is not null)
                        apps.Add(new AllowedAppDto(appKind, entry.Value));
                    break;

                case BundleEntryKind.Domain:
                    var domainType = MapDomainMatchType(entry.MatchType);
                    if (domainType is not null)
                        domains.Add(new AllowedDomainDto(domainType, entry.Value));
                    break;
            }
        }

        return new ExpandedAllowlist(DedupeApps(apps), DedupeDomains(domains));
    }

    private static string? MapAppMatchKind(BundleEntryMatchType matchType) => matchType switch
    {
        BundleEntryMatchType.Exact => AllowedAppMatchKinds.ProcessName,
        BundleEntryMatchType.SignedPublisher => AllowedAppMatchKinds.Publisher,
        // Wildcard/Suffix don't have a sensible mapping for the agent's
        // process/path/publisher model — skipped rather than silently
        // narrowed. Catalogue editor should not produce these combinations.
        _ => null,
    };

    private static string? MapDomainMatchType(BundleEntryMatchType matchType) => matchType switch
    {
        BundleEntryMatchType.Exact => AllowedDomainMatchTypes.Exact,
        BundleEntryMatchType.Wildcard => AllowedDomainMatchTypes.Wildcard,
        BundleEntryMatchType.Suffix => AllowedDomainMatchTypes.Suffix,
        // SignedPublisher is meaningless for a domain — skip.
        _ => null,
    };

    private static IReadOnlyList<AllowedAppDto> DedupeApps(IEnumerable<AllowedAppDto> source)
    {
        var seen = new HashSet<(string, string)>();
        var result = new List<AllowedAppDto>();
        foreach (var dto in source)
        {
            if (string.IsNullOrWhiteSpace(dto.Value)) continue;
            var key = (dto.MatchKind, dto.Value.Trim().ToLowerInvariant());
            if (seen.Add(key))
                result.Add(dto);
        }
        return result;
    }

    private static IReadOnlyList<AllowedDomainDto> DedupeDomains(IEnumerable<AllowedDomainDto> source)
    {
        var seen = new HashSet<(string, string)>();
        var result = new List<AllowedDomainDto>();
        foreach (var dto in source)
        {
            if (string.IsNullOrWhiteSpace(dto.Value)) continue;
            var key = (dto.MatchType, dto.Value.Trim().ToLowerInvariant());
            if (seen.Add(key))
                result.Add(dto);
        }
        return result;
    }
}
