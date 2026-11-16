using System.Text.Json.Serialization;

namespace Anchor.Domain.Events;

// Serialized by name on the wire so the dashboard reads readable kinds
// (e.g. "ForegroundChange") rather than the default numeric ordinal (#306).
// Mirrors the other wire enums (BundleEntryKind, BundleEntryMatchType, UserRole).
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EventKind
{
    ForegroundChange,
    BlockedUrl,
    UnblockRequest,
    HeartbeatLost,
    AgentReconnected,
    AgentKilled,
    ManualLeave,
    JoinDeclined,

    /// <summary>
    /// A teacher approved a pending unblock request (#101). The payload records
    /// which scope was used — <c>{"host":"…","scope":"student"|"class"}</c> — so
    /// the bundle-expansion signal (§7.4) can be reviewed after the session.
    /// </summary>
    UnblockApproved,

    /// <summary>
    /// A tamper attempt was observed on a student's machine during an active
    /// session (#105). Soft-enforcement posture (design §5.4): we cannot prevent
    /// it, only make it visible to the teacher. The payload names the kind —
    /// <c>{"kind":"inprivate_opened"|"host_permission_revoked"|…}</c> — so one
    /// event covers every signal, whether reported by the extension (in-browser)
    /// or, later, by the agent acting as on-box witness. Reported via the hub's
    /// <c>ReportEvent</c> and pushed to the teacher's live roster.
    /// </summary>
    TamperDetected,

    /// <summary>
    /// A joined student's agent sent no heartbeat within the join grace, so
    /// apps weren't being enforced on their device (#444): typically the Edge
    /// extension joined without the agent. Recorded by the backend's heartbeat
    /// monitor, not reported by a client. The payload is
    /// <c>{"expectedSince":"…"}</c>, when the agent was first expected. An agent
    /// that turns up later is recorded as <see cref="AgentReconnected"/>.
    /// </summary>
    AgentMissing,

    /// <summary>
    /// A class member hadn't joined the session once the join grace had passed
    /// since it started, so nothing was being enforced for them (#493): their
    /// laptop has no working Anchor, or they weren't there. Recorded by the
    /// backend's heartbeat monitor, at most once per student and session. The
    /// payload is <c>{"expectedSince":"…"}</c>, the session's start. A student
    /// who joins later is recorded only by their participant row's join.
    /// </summary>
    NotConnected
}
