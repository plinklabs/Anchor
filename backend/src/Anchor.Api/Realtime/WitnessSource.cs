namespace Anchor.Api.Realtime;

/// <summary>
/// Which on-box witness a heartbeat came from (#149). The agent and the Edge
/// extension share the same <c>userId</c>, so the <see cref="HeartbeatTracker"/>
/// keys liveness by source as well — otherwise the extension's pings would keep
/// the agent's <see cref="Anchor.Domain.Events.EventKind.HeartbeatLost"/> rule
/// warm (and vice-versa), masking a dead witness.
/// </summary>
public enum WitnessSource
{
    /// <summary>
    /// The on-box FocusAgent. Drives HeartbeatLost / AgentReconnected, and
    /// AgentMissing when it never pings after its student joined (#444).
    /// </summary>
    Agent,

    /// <summary>
    /// The in-browser Edge extension. No longer recorded (#569): the retired
    /// <c>ExtensionHeartbeat</c> takes the call and records nothing, and the
    /// monitor reports nothing for it. #578 removes it.
    /// </summary>
    Extension,
}
