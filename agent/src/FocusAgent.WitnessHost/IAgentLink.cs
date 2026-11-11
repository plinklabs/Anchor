namespace FocusAgent.WitnessHost;

/// <summary>
/// The host's link to the running FocusAgent over the local named pipe. Behind
/// an interface so <see cref="WitnessBridge"/> — which relays the agent's
/// up/down state and messages to the browser — is unit-testable without a real
/// pipe.
/// </summary>
public interface IAgentLink
{
    /// <summary>Raised when the pipe to the agent (re)connects.</summary>
    event Action? Connected;

    /// <summary>Raised when the pipe to the agent drops (the agent died/stopped).</summary>
    event Action? Disconnected;

    /// <summary>
    /// Raised for each line of JSON the agent sends for the extension (#489): the
    /// session it is in, its allowlist changes, answers to the extension's requests.
    /// </summary>
    event Action<string>? MessageReceived;

    /// <summary>Starts the background connect-and-retry loop.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Best-effort forward of a line to the agent. While the link is down the line
    /// is kept (a bounded few) for the next connection, so a report the extension
    /// makes just as the host starts still arrives; the agent's liveness is
    /// signalled by <see cref="Disconnected"/>.
    /// </summary>
    Task SendAsync(string line, CancellationToken ct = default);

    Task StopAsync();
}
