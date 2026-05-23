namespace Anchor.Api.Realtime;

public sealed record JoinSessionRequest(Guid SessionId, string? JoinCode);

public sealed record JoinSessionResult(Guid SessionId, Guid UserId);

public sealed record ReportEventRequest(Guid SessionId, string Kind, string? PayloadJson, DateTimeOffset? OccurredAt);

public sealed record DeclineSessionRequest(Guid SessionId, string? Reason);

public sealed record SessionStartedPayload(
    Guid SessionId,
    Guid ClassId,
    string Mode,
    DateTimeOffset StartedAt,
    string JoinCode);

public sealed record BundleUpdatedPayload(Guid SessionId, Guid BundleId);
