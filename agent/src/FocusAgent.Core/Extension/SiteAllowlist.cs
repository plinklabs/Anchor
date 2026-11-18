using FocusAgent.Core.Dtos;

namespace FocusAgent.Core.Extension;

/// <summary>
/// Whether a session's domains allow a host, judged the way the Edge extension
/// judges a navigation (<c>isUrlAllowed</c> in
/// <c>extension/src/shared/host-matcher.ts</c>), so the agent's second look at
/// Edge (#526) and the extension never disagree about what is on the list:
/// <list type="bullet">
/// <item><c>Exact</c>: the host itself, ignoring case.</item>
/// <item><c>Wildcard</c> and <c>Suffix</c>: the suffix and every subdomain of it,
/// with or without the leading <c>*.</c>.</item>
/// <item>Anything else matches nothing.</item>
/// </list>
/// </summary>
public static class SiteAllowlist
{
    /// <summary>True when one of <paramref name="domains"/> allows <paramref name="host"/>.</summary>
    public static bool Allows(IEnumerable<AllowedDomainDto> domains, string host)
    {
        var hostname = host.Trim().ToLowerInvariant();
        if (hostname.Length == 0) return false;
        foreach (var domain in domains)
        {
            if (Matches(hostname, domain)) return true;
        }
        return false;
    }

    private static bool Matches(string hostname, AllowedDomainDto domain)
    {
        var raw = domain.Value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(raw)) return false;

        switch (domain.MatchType)
        {
            case "Exact":
                return hostname == raw;
            case "Wildcard":
            case "Suffix":
                var suffix = raw.StartsWith("*.", StringComparison.Ordinal) ? raw[2..] : raw;
                if (suffix.Length == 0) return false;
                return hostname == suffix || hostname.EndsWith("." + suffix, StringComparison.Ordinal);
            default:
                return false;
        }
    }
}
