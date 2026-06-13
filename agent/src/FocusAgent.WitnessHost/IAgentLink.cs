namespace FocusAgent.WitnessHost;

/// <summary>
/// The host's link to the running FocusAgent over the local named pipe. Behind
/// an interface so <see cref="WitnessBridge"/> — which relays the agent's
/// up/down state to the browser — is unit-testable without a real pipe.
/// </summary>
public interface IAgentLink
{
    /// <summary>Raised when the pipe to the agent (re)connects.</summary>
    event Action? Connected;

    /// <summary>Raised when the pipe to the agent drops (the agent died/stopped).</summary>
    event Action? Disconnected;

    /// <summary>Starts the background connect-and-retry loop.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Best-effort forward of a line to the agent. A no-op when the link is down —
    /// the agent's liveness is already signalled by <see cref="Disconnected"/>.
    /// </summary>
    Task SendAsync(string line, CancellationToken ct = default);

    Task StopAsync();
}
