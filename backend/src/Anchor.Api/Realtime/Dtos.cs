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
    string JoinCode,
    IReadOnlyList<AllowedAppDto> Apps,
    IReadOnlyList<AllowedDomainDto> Domains);

/// <summary>
/// One entry in the session allowlist's app list (#70). <see cref="MatchKind"/>
/// is a wire-string matching the agent's <c>AllowedAppMatchKind</c> enum
/// (ProcessName / ExecutablePath / Publisher).
/// </summary>
public sealed record AllowedAppDto(string MatchKind, string Value);

/// <summary>
/// One entry in the session allowlist's domain list (#70). <see cref="MatchType"/>
/// is a wire-string matching <c>BundleEntryMatchType</c>
/// (Exact / Wildcard / Suffix). Consumed by the Edge extension once it lands.
/// </summary>
public sealed record AllowedDomainDto(string MatchType, string Value);

public sealed record BundleUpdatedPayload(Guid SessionId, Guid BundleId);

public sealed record HeartbeatLostPayload(Guid SessionId, Guid UserId, DateTimeOffset LastSeenAt);

public sealed record AgentReconnectedPayload(Guid SessionId, Guid UserId, DateTimeOffset ReconnectedAt);

/// <summary>
/// Delta-shaped session allowlist amendment (#73). Pushed to the granted
/// student's user group when a teacher approves a pending UnblockRequest.
/// The extension merges <see cref="AddedDomains"/> into its cached allowlist;
/// the agent does not subscribe (the agent only enforces apps, not URLs).
/// </summary>
public sealed record AllowlistAmendedPayload(
    Guid SessionId,
    Guid UserId,
    IReadOnlyList<AllowedDomainDto> AddedDomains);

/// <summary>
/// Live notification that a student has clicked "Request access" on the
/// extension's block page. Pushed to the session group so the teacher's
/// dashboard can show the pending request without polling. The corresponding
/// <see cref="Anchor.Domain.Events.EventKind.UnblockRequest"/> event is also
/// persisted so requests can be reconciled with the GET endpoint after a
/// dashboard reload.
/// </summary>
public sealed record UnblockRequestedPayload(
    Guid SessionId,
    Guid UserId,
    string UserDisplayName,
    string Host,
    string Url,
    DateTimeOffset RequestedAt);
