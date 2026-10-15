using Anchor.Api.Realtime;

namespace Anchor.Api.Sessions;

/// <summary>
/// Single source-of-truth for the always-allowed apps and domains every
/// session receives, irrespective of which bundles the teacher picked.
/// Section 7.2 of focus-system-design.md. The agent's AllowlistMatcher
/// used to carry these locally (#70) — that copy is gone; both the agent
/// payload and any future "what's in this session" debug endpoint pull
/// from here so backend and agent can't drift.
/// </summary>
public static class SessionAllowlist
{
    public static readonly IReadOnlyList<AllowedAppDto> BaselineApps = new[]
    {
        new AllowedAppDto(AllowedAppMatchKinds.ProcessName, "msedge"),
        new AllowedAppDto(AllowedAppMatchKinds.ProcessName, "explorer"),
    };

    public static readonly IReadOnlyList<AllowedDomainDto> BaselineDomains = new[]
    {
        new AllowedDomainDto(AllowedDomainMatchTypes.Wildcard, "*.microsoftonline.com"),
        new AllowedDomainDto(AllowedDomainMatchTypes.Wildcard, "*.office.com"),
        new AllowedDomainDto(AllowedDomainMatchTypes.Wildcard, "*.office365.com"),
        new AllowedDomainDto(AllowedDomainMatchTypes.Wildcard, "*.microsoft.com"),
        new AllowedDomainDto(AllowedDomainMatchTypes.Wildcard, "*.live.com"),
        new AllowedDomainDto(AllowedDomainMatchTypes.Wildcard, "*.windows.net"),
        new AllowedDomainDto(AllowedDomainMatchTypes.Exact, "fonts.googleapis.com"),
        new AllowedDomainDto(AllowedDomainMatchTypes.Exact, "fonts.gstatic.com"),
    };

    /// <summary>
    /// Extra always-allowed apps present ONLY in a Development build, so a
    /// session (manual testing or an agent e2e run against a Development
    /// backend) can't lock a developer out of the windows they work in: VS Code
    /// (#125) and Claude (#372). Gated behind
    /// <c>IHostEnvironment.IsDevelopment()</c> in <see cref="SessionAllowlistExpander"/>;
    /// a non-Development (Release) build must never receive these. The agent
    /// normalizes process names case-insensitively and strips <c>.exe</c>, so
    /// "Code" matches <c>Code.exe</c> and "claude" matches <c>claude.exe</c> —
    /// both the Claude desktop app and the Claude Code binary it runs.
    /// </summary>
    public static readonly IReadOnlyList<AllowedAppDto> DevelopmentApps = new[]
    {
        new AllowedAppDto(AllowedAppMatchKinds.ProcessName, "Code"),
        new AllowedAppDto(AllowedAppMatchKinds.ProcessName, "claude"),
    };

    /// <summary>
    /// Extra always-allowed domains present ONLY in a Development build (#125),
    /// so the dashboard (<c>:5173</c>) and backend (<c>:5276</c>) stay reachable
    /// to stop an active session. Matching is hostname-only/port-agnostic, so
    /// the bare host covers every local port. Same Development gate and Release
    /// exclusion as <see cref="DevelopmentApps"/>.
    /// </summary>
    public static readonly IReadOnlyList<AllowedDomainDto> DevelopmentDomains = new[]
    {
        new AllowedDomainDto(AllowedDomainMatchTypes.Exact, "localhost"),
        new AllowedDomainDto(AllowedDomainMatchTypes.Exact, "127.0.0.1"),
    };

    /// <summary>
    /// The configuration key listing the dashboard's origin(s). The CORS policy
    /// in Program.cs and <see cref="DashboardDomains"/> read the same list.
    /// </summary>
    public const string DashboardOriginsKey = "Cors:AllowedOrigins";

    /// <summary>
    /// The dashboard's own host(s), always allowed in every build (#409), so a
    /// teacher who runs the agent on their own laptop can still pick bundles and
    /// end the session. Taken from <see cref="DashboardOriginsKey"/>: the
    /// dashboard can't call the API from an origin that isn't listed there, so
    /// every working deployment already names the host teachers use, custom
    /// domain included. Students may reach it too; they can't do anything there.
    /// Matching is hostname-only and port-agnostic, so each origin is reduced to
    /// its bare host; an entry that isn't an http(s) origin is skipped.
    /// </summary>
    public static IReadOnlyList<AllowedDomainDto> DashboardDomains(IEnumerable<string?> origins)
    {
        var domains = new List<AllowedDomainDto>();
        foreach (var origin in origins)
        {
            if (!Uri.TryCreate(origin?.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
                string.IsNullOrEmpty(uri.IdnHost))
            {
                continue;
            }

            // IdnHost: lowercase, and punycode for an internationalized name,
            // which is what the extension reads from a URL's hostname.
            domains.Add(new AllowedDomainDto(AllowedDomainMatchTypes.Exact, uri.IdnHost));
        }
        return domains;
    }
}

/// <summary>
/// Wire string values for <see cref="AllowedAppDto.MatchKind"/>. Mirrors
/// the agent's <c>AllowedAppMatchKind</c> enum names.
/// </summary>
public static class AllowedAppMatchKinds
{
    public const string ProcessName = "ProcessName";
    public const string ExecutablePath = "ExecutablePath";
    public const string Publisher = "Publisher";
}

/// <summary>
/// Wire string values for <see cref="AllowedDomainDto.MatchType"/>. Names
/// match the persisted <c>BundleEntryMatchType</c> enum so a future Edge
/// extension can switch on them without an extra translation layer.
/// </summary>
public static class AllowedDomainMatchTypes
{
    public const string Exact = "Exact";
    public const string Wildcard = "Wildcard";
    public const string Suffix = "Suffix";
}
