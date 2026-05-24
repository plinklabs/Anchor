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
