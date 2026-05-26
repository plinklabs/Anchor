namespace FocusAgent.Core.Dtos;

public sealed record SessionStartedPayload(
    Guid SessionId,
    Guid ClassId,
    DateTimeOffset StartedAt,
    string JoinCode,
    IReadOnlyList<AllowedAppDto> Apps,
    IReadOnlyList<AllowedDomainDto> Domains);
