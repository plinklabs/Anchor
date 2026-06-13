namespace Anchor.Api.Realtime;

public sealed record JoinSessionRequest(Guid SessionId, string? JoinCode);

public sealed record JoinSessionResult(Guid SessionId, Guid UserId);

public sealed record ReportEventRequest(Guid SessionId, string Kind, string? PayloadJson, DateTimeOffset? OccurredAt);

public sealed record DeclineSessionRequest(Guid SessionId, string? Reason);

public sealed record SessionStartedPayload(
    Guid SessionId,
    Guid ClassId,
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
/// (Exact / Wildcard / Suffix). Consumed by the Edge extension.
/// </summary>
public sealed record AllowedDomainDto(string MatchType, string Value);

/// <summary>
/// Full replacement of one student's session allowlist after the teacher
/// changes the session's bundles mid-session (#93). Carries the recomputed
/// baseline + bundle apps/domains, with the student's own unblock grants (#73)
/// already folded into <see cref="Domains"/>. Pushed to the student's user
/// group so both their agent (apps) and extension (domains) update. Unlike
/// <see cref="AllowlistAmendedPayload"/> (a delta), this is the authoritative
/// new allowlist — consumers replace, not merge.
/// </summary>
public sealed record SessionBundlesUpdatedPayload(
    Guid SessionId,
    IReadOnlyList<AllowedAppDto> Apps,
    IReadOnlyList<AllowedDomainDto> Domains);

/// <summary>
/// The teacher-facing live state of one class member in a session (#100).
/// Serialised as its name on the wire and mirrored by the dashboard. The
/// taxonomy lives here so the staleness threshold (<see cref="HeartbeatOptions"/>)
/// and the state machine have a single home; the dashboard only renders + sorts.
/// </summary>
public enum ParticipantLiveState
{
    /// <summary>In the class roster but no join recorded for this session.</summary>
    NeverJoined,

    /// <summary>Joined and heartbeat fresh.</summary>
    Joined,

    /// <summary>Joined but no heartbeat for longer than the configured timeout.</summary>
    HeartbeatStale,

    /// <summary>Cancelled during the join-confirmation toast.</summary>
    Declined,

    /// <summary>Manually left or the agent stopped reporting after having joined.</summary>
    Left,
}

/// <summary>
/// Live notification that a class member's lifecycle state changed
/// (joined / declined / left) so the teacher's roster updates without polling
/// (#100). Heartbeat-driven transitions are carried by the existing
/// <see cref="HeartbeatLostPayload"/> / <see cref="AgentReconnectedPayload"/>
/// instead. Pushed to the session group.
/// </summary>
public sealed record ParticipantStateChangedPayload(
    Guid SessionId,
    Guid UserId,
    string DisplayName,
    string State,
    DateTimeOffset At);

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

/// <summary>
/// Live notification that a tamper attempt was observed on a student's machine
/// during an active session (#105). Pushed to the session group so the teacher's
/// live roster can flag the student without polling. The corresponding
/// <see cref="Anchor.Domain.Events.EventKind.TamperDetected"/> event is also
/// persisted, so the flag is recoverable from the roster snapshot after a
/// dashboard reload. <see cref="Kind"/> is the tamper sub-kind reported by the
/// client (e.g. <c>inprivate_opened</c>, <c>host_permission_revoked</c>);
/// unknown values are surfaced verbatim rather than rejected, since the goal is
/// visibility, not classification (design §5.4).
/// </summary>
public sealed record TamperDetectedPayload(
    Guid SessionId,
    Guid UserId,
    string UserDisplayName,
    string Kind,
    DateTimeOffset DetectedAt);
