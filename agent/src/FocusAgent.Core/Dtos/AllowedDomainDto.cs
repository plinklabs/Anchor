namespace FocusAgent.Core.Dtos;

/// <summary>
/// One entry in the session allowlist's domain list. Wire-shape twin of the
/// backend's <c>AllowedDomainDto</c>. Consumed by the Edge extension once it
/// lands (#70 leaves that work out of scope). The agent carries the data
/// across the wire so the extension can pick it up via a co-located
/// channel without re-querying the backend.
/// </summary>
public sealed record AllowedDomainDto(string MatchType, string Value);
