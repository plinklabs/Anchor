namespace FocusAgent.Core.Dtos;

/// <summary>
/// Wire-shape twin of the backend's <c>SessionAllowlistPayload</c> (#570): the
/// student's whole allowlist in a running session, as the agent asks for it
/// after a reconnect. The same lists a <see cref="SessionBundlesUpdatedPayload"/>
/// carries, replaced the same way.
/// </summary>
public sealed record SessionAllowlistPayload(
    Guid SessionId,
    IReadOnlyList<AllowedAppDto> Apps,
    IReadOnlyList<AllowedDomainDto> Domains);
