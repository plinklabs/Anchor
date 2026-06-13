namespace FocusAgent.Core.Tamper;

/// <summary>
/// The agent's side of the native-messaging witness link to the browser
/// extension (#146 part 1). The transport owns the OS plumbing — a named-pipe
/// server the browser-launched native host connects to — and surfaces just the
/// liveness transitions the <see cref="ExtensionWitnessMonitor"/> reasons about:
/// the extension's witness connected, or dropped.
///
/// Split out as an interface so the monitor's decision logic (drop during an
/// active session → report) is unit-testable with a fake transport, the same
/// way <c>ISessionHubConnection</c> lets the heartbeat pump be tested headless.
/// </summary>
public interface IExtensionWitnessTransport
{
    /// <summary>Raised when the extension's native host connects (link is up).</summary>
    event EventHandler? WitnessConnected;

    /// <summary>
    /// Raised when the link drops: the host process exited because the browser
    /// closed its stdin, which happens when the extension is disabled, removed,
    /// or the browser shuts down. This is the signal the extension cannot send
    /// about itself.
    /// </summary>
    event EventHandler? WitnessDisconnected;

    /// <summary>Begins listening for the native host to connect.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Stops listening and tears down any active link.</summary>
    Task StopAsync(CancellationToken ct = default);
}
