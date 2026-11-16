namespace FocusAgent.Core.Tamper;

/// <summary>
/// The witness link as a two-way channel (#489). The extension no longer has a
/// backend connection or a sign-in of its own: the agent hands it the session
/// over this link, and the extension hands back what it reports (blocked URLs,
/// unblock requests, its in-browser tamper signals) for the agent to send with
/// its own connection.
///
/// Each message is one line of JSON, the same object the extension posts or
/// receives over native messaging; the native host forwards it unchanged in
/// both directions. Split from <see cref="IExtensionWitnessTransport"/> so the
/// consumers that only watch liveness (<see cref="ExtensionWitnessMonitor"/>,
/// <see cref="ExtensionGuard"/>) keep their narrow seam.
///
/// With one extension per Edge profile (#517) there are two ways to send:
/// <see cref="Broadcast"/> for what every profile must know (the session, its
/// allowlist, its end) and <see cref="TrySend"/> for what belongs to one of
/// them (the state on connect, the answer to its own request).
/// </summary>
public interface IExtensionWitnessChannel : IExtensionWitnessTransport
{
    /// <summary>
    /// Raised for each line an extension sends, on that connection's read
    /// thread. Only raised between that connection's
    /// <see cref="IExtensionWitnessTransport.WitnessConnected"/> and
    /// <see cref="IExtensionWitnessTransport.WitnessDisconnected"/>.
    /// </summary>
    event EventHandler<WitnessMessage>? MessageReceived;

    /// <summary>
    /// Queues one line of JSON for one extension. Never blocks: lines go out in
    /// the order they were queued. Returns false, and drops the line, when that
    /// connection is gone; the agent hands the current session down again on
    /// every connect, so nothing needs to wait for the next one.
    /// </summary>
    bool TrySend(WitnessConnection connection, string line);

    /// <summary>
    /// Queues one line of JSON for every connected extension, and returns how
    /// many that was (0 when none is connected, and the line is dropped). A
    /// connection is included from just before its
    /// <see cref="IExtensionWitnessTransport.WitnessConnected"/> is raised, so
    /// nothing broadcast after that event can miss it.
    /// </summary>
    int Broadcast(string line);
}
