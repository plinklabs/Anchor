namespace Anchor.Domain.Events;

public enum EventKind
{
    ForegroundChange,
    BlockedUrl,
    UnblockRequest,
    HeartbeatLost,
    AgentKilled,
    ManualLeave,
    JoinDeclined
}
