namespace FocusAgent.Core.Dtos;

/// <summary>
/// One entry in the session allowlist's app list. Wire-shape twin of the
/// backend's <c>AllowedAppDto</c>. <see cref="MatchKind"/> is a string
/// matching <see cref="Focus.AllowedAppMatchKind"/> enum names so the
/// agent doesn't need to take a hard dep on a generated proto layer.
/// </summary>
public sealed record AllowedAppDto(string MatchKind, string Value);
