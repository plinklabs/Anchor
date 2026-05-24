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
    JoinDeclined
}
