namespace FocusAgent.Core.Dtos;

/// <summary>
/// Wire-shape twin of the backend's <c>AllowlistAmendedPayload</c> (#73): the
/// teacher approved this student's unblock request, so <see cref="AddedDomains"/>
/// joins the session's allowlist for them. Pushed to the student's user group as
/// a delta. The agent doesn't filter URLs itself; it hands the delta down to the
/// co-located extension over the witness link (#489).
/// </summary>
public sealed record AllowlistAmendedPayload(
    Guid SessionId,
    Guid UserId,
    IReadOnlyList<AllowedDomainDto> AddedDomains);
