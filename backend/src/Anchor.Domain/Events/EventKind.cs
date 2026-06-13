namespace Anchor.Domain.Events;

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
    TamperDetected
}
