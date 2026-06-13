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
    /// <summary>The on-box FocusAgent. Drives HeartbeatLost / AgentReconnected.</summary>
    Agent,

    /// <summary>
    /// The in-browser Edge extension. Going silent mid-session is the
    /// witness-independent absence-net: it surfaces as a
    /// <c>TamperDetected{kind:"extension_silent"}</c> rather than HeartbeatLost.
    /// </summary>
    Extension,
}
