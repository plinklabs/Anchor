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
    /// Extra always-allowed apps present ONLY in a Development build (#125),
    /// so a session can't lock a developer out of their editor. Gated behind
    /// <c>IHostEnvironment.IsDevelopment()</c> in <see cref="SessionAllowlistExpander"/>;
    /// a non-Development (Release) build must never receive these. The agent
    /// normalizes process names case-insensitively and strips <c>.exe</c>, so
    /// "Code" matches <c>Code.exe</c>.
    /// </summary>
    public static readonly IReadOnlyList<AllowedAppDto> DevelopmentApps = new[]
    {
        new AllowedAppDto(AllowedAppMatchKinds.ProcessName, "Code"),
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
