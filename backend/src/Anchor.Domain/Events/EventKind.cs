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
    UnblockApproved
}
