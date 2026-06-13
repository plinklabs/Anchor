namespace Anchor.Api.Realtime;

/// <summary>
/// Tamper sub-kinds the <em>backend</em> originates (#149), as opposed to the
/// in-browser kinds the extension reports and the on-box kinds the agent reports
/// (both of those are wire-strings the backend only ever surfaces verbatim, see
/// the agent's <c>FocusAgent.Core.Tamper.TamperKinds</c>). These are the
/// witness-independent absence-net: the backend names them itself because no
/// client is alive to report them.
/// </summary>
public static class TamperKinds
{
    /// <summary>
    /// The extension witness went silent during an active session — its hub
    /// heartbeat stopped for longer than the staleness window (browser closed,
    /// or the worker died). This is the net for the case the native-messaging
    /// witness (#146) cannot cover: when the pipe link never existed, the agent
    /// can't tell "extension disabled" from "never connected". Emitted by
    /// <see cref="HeartbeatMonitor"/>, not by any client.
    /// </summary>
    public const string ExtensionSilent = "extension_silent";
}
