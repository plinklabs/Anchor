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
/// </summary>
public interface IExtensionWitnessChannel : IExtensionWitnessTransport
{
    /// <summary>
    /// Raised for each line the extension sends, on the transport's read thread.
    /// Only raised between <see cref="IExtensionWitnessTransport.WitnessConnected"/>
    /// and <see cref="IExtensionWitnessTransport.WitnessDisconnected"/>.
    /// </summary>
    event EventHandler<string>? MessageReceived;

    /// <summary>
    /// Queues one line of JSON for the extension. Never blocks: lines go out in
    /// the order they were queued. Returns false, and drops the line, when no
    /// extension is connected; the agent hands the current session down again on
    /// every connect, so nothing needs to wait for the next one.
    /// </summary>
    bool TrySend(string line);
}
