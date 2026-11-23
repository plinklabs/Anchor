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

    /// <summary>
    /// What every participant of the session shares: its bundles and its
    /// whole-class grants (#101). Not what a student gets — that is
    /// <see cref="ExpandForParticipantAsync"/>.
    /// </summary>
    Task<ExpandedAllowlist> ExpandForSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The allowlist one student enforces in a running session: the session's
    /// bundles, its whole-class grants and that student's own grants (#73), and
    /// never another student's. Every path that hands a student the whole
    /// allowlist of a running session (rejoin, catch-up, join by code, bundle
    /// change) uses this, so a student's own grants survive each of them (#568).
    /// </summary>
    Task<ExpandedAllowlist> ExpandForParticipantAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="ExpandForParticipantAsync"/> for several students at once, one
    /// entry per user id, in a fixed number of queries.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ExpandedAllowlist>> ExpandForParticipantsAsync(
        Guid sessionId, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default);
}

public sealed record ExpandedAllowlist(
    IReadOnlyList<AllowedAppDto> Apps,
    IReadOnlyList<AllowedDomainDto> Domains);

public sealed class SessionAllowlistExpander : ISessionAllowlistExpander
{
    private readonly AnchorDbContext _db;
    private readonly bool _includeDevelopmentCarveouts;
    private readonly IReadOnlyList<AllowedDomainDto> _dashboardDomains;

    public SessionAllowlistExpander(AnchorDbContext db, IHostEnvironment environment, IConfiguration configuration)
    {
        _db = db;
        // Dev-only carve-outs: localhost + VS Code (#125), Claude (#372). Computed
        // once here so a Release build never merges them, no matter which
        // expansion path runs.
        _includeDevelopmentCarveouts = environment.IsDevelopment();
        // The dashboard's own host(s) (#409), in every build and on every path.
        _dashboardDomains = SessionAllowlist.DashboardDomains(
            configuration.GetSection(SessionAllowlist.DashboardOriginsKey).Get<string[]>() ?? Array.Empty<string>());
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

        var merged = Merge(entries);

        // Whole-class unblock grants (#101) apply to everyone in the session, so
        // a rejoin (#54) or join-by-code rehydration must include them — without
        // this a late joiner or a reconnecting agent would miss a host the
        // teacher already opened for the class. Per-student grants are NOT folded
        // here: they're scoped to one user, see ExpandForParticipantsAsync.
        var classHosts = await _db.SessionWideUnblockGrants
            .AsNoTracking()
            .Where(g => g.SessionId == sessionId)
            .Select(g => g.Host)
            .ToListAsync(cancellationToken);
        if (classHosts.Count == 0)
            return merged;

        var domains = new List<AllowedDomainDto>(merged.Domains);
        foreach (var host in classHosts)
            domains.Add(new AllowedDomainDto(AllowedDomainMatchTypes.Suffix, host));

        return new ExpandedAllowlist(merged.Apps, DedupeDomains(domains));
    }

    public async Task<ExpandedAllowlist> ExpandForParticipantAsync(
        Guid sessionId, Guid userId, CancellationToken cancellationToken = default)
    {
        var expanded = await ExpandForParticipantsAsync(sessionId, new[] { userId }, cancellationToken);
        return expanded[userId];
    }

    public async Task<IReadOnlyDictionary<Guid, ExpandedAllowlist>> ExpandForParticipantsAsync(
        Guid sessionId, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
    {
        var shared = await ExpandForSessionAsync(sessionId, cancellationToken);
        var result = new Dictionary<Guid, ExpandedAllowlist>();
        if (userIds.Count == 0)
            return result;

        var hostsByUser = (await _db.SessionUnblockGrants
                .AsNoTracking()
                .Where(g => g.SessionId == sessionId && userIds.Contains(g.UserId))
                .Select(g => new { g.UserId, g.Host })
                .ToListAsync(cancellationToken))
            .GroupBy(g => g.UserId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Host).ToList());

        foreach (var userId in userIds)
        {
            if (!hostsByUser.TryGetValue(userId, out var hosts) || hosts.Count == 0)
            {
                result[userId] = shared;
                continue;
            }

            // Suffix match mirrors the shape the unblock approval pushes, so the
            // host covers its subdomains here as it did when it was granted.
            var domains = new List<AllowedDomainDto>(shared.Domains);
            foreach (var host in hosts)
                domains.Add(new AllowedDomainDto(AllowedDomainMatchTypes.Suffix, host));
            result[userId] = new ExpandedAllowlist(shared.Apps, DedupeDomains(domains));
        }

        return result;
    }

    private ExpandedAllowlist Merge(IReadOnlyCollection<BundleEntry> entries)
    {
        var apps = new List<AllowedAppDto>(SessionAllowlist.BaselineApps);
        var domains = new List<AllowedDomainDto>(SessionAllowlist.BaselineDomains);
        domains.AddRange(_dashboardDomains);

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
