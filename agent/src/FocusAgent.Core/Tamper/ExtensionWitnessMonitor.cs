using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusAgent.Core.Tamper;

/// <summary>
/// Keeps track of the extension witness links (#146 part 1). The extension
/// cannot witness its own disablement or removal, so the agent is the on-box
/// witness: the native-messaging link of an extension that goes away drops.
///
/// A drop alone says little, though. The link drops the same way when the
/// extension is restarted (an option changed on its page, an update, a reload),
/// when Edge is closed, and when one profile's last window is closed (#518). So
/// the monitor only reports what the links do, and <see cref="ExtensionGuard"/>
/// decides what a drop means: it waits a grace period, looks whether Edge is
/// still on screen, and only then pauses Edge (#427) and tells the teacher.
///
/// Every Edge profile has its own copy of the extension and its own link (#517),
/// so the monitor keeps the set of open <see cref="Connections"/> and raises
/// <see cref="ConnectionOpened"/> / <see cref="ConnectionClosed"/> for each one.
/// "The extension is there" (<see cref="IsConnected"/>,
/// <see cref="ConnectionChanged"/>) means at least one of them is connected. One
/// profile's link closing while another is still up is usually that profile's
/// last window being closed; the extension being switched off there instead is
/// found by <see cref="OpenProfileExtensionWatch"/>, which the links can't show.
/// </summary>
public sealed class ExtensionWitnessMonitor : IAsyncDisposable
{
    private readonly IExtensionWitnessTransport _transport;
    private readonly ILogger<ExtensionWitnessMonitor> _log;
    private readonly object _gate = new();

    // Held while a connect or drop is applied and announced, so two connections
    // changing at once (each on its own transport thread) can't deliver their
    // ConnectionChanged out of order — a stale "false" after the "true" would
    // have the guard pause Edge with an extension connected.
    private readonly object _announceGate = new();
    private readonly Dictionary<long, WitnessConnection> _connections = new();
    private bool _hasConnected;

    /// <param name="transport">The witness link surfacing connect/disconnect.</param>
    public ExtensionWitnessMonitor(
        IExtensionWitnessTransport transport,
        ILogger<ExtensionWitnessMonitor>? log = null)
    {
        _transport = transport;
        _log = log ?? NullLogger<ExtensionWitnessMonitor>.Instance;

        _transport.WitnessConnected += OnConnected;
        _transport.WitnessDisconnected += OnDisconnected;
    }

    public Task StartAsync(CancellationToken ct = default) => _transport.StartAsync(ct);

    /// <summary>
    /// True while at least one extension's witness host is connected — i.e. the
    /// extension is installed and running in some Edge profile.
    /// <see cref="FocusAgent.Core.Extension.ExtensionSelfRegistrar"/>
    /// reads this as the success signal for the force-install policy (#211): if it
    /// stays false past the grace period (which starts once Edge runs) and the
    /// extension isn't installed in an Edge profile either (#488), the policy
    /// didn't take and the guided install is shown.
    /// </summary>
    public bool IsConnected
    {
        get { lock (_gate) return _connections.Count > 0; }
    }

    /// <summary>
    /// True once the extension's witness has connected at least once since the
    /// agent started — i.e. this device has the extension. A device where it never
    /// connected (not installed yet, or the host isn't registered) is one the agent
    /// can't tell "switched off" from "never there", so the Edge pause (#427)
    /// leaves it alone.
    /// </summary>
    public bool HasConnected
    {
        get { lock (_gate) return _hasConnected; }
    }

    /// <summary>
    /// Raised after each genuine transition of <see cref="IsConnected"/>: true when
    /// the first witness comes up, false when the last one drops. Raised on a
    /// transport thread, in or out of a session, never two at once and always in
    /// the order the transitions happened.
    /// </summary>
    public event EventHandler<bool>? ConnectionChanged;

    /// <summary>The connections open right now: one per Edge profile with the extension running.</summary>
    public IReadOnlyList<WitnessConnection> Connections
    {
        get { lock (_gate) return _connections.Values.ToArray(); }
    }

    /// <summary>
    /// Raised when one extension's link comes up, before any
    /// <see cref="ConnectionChanged"/> it causes, with the same threading.
    /// </summary>
    public event EventHandler<WitnessConnection>? ConnectionOpened;

    /// <summary>
    /// Raised when one extension's link drops, whether or not others are still
    /// connected, before any <see cref="ConnectionChanged"/> it causes, with the
    /// same threading.
    /// </summary>
    public event EventHandler<WitnessConnection>? ConnectionClosed;

    private void OnConnected(object? sender, WitnessConnection connection)
    {
        lock (_announceGate)
        {
            bool added, first;
            int open;
            lock (_gate)
            {
                first = _connections.Count == 0;
                added = _connections.TryAdd(connection.Id, connection);
                open = _connections.Count;
                _hasConnected = true;
            }
            // The same connection announced twice is not a new one.
            if (!added) return;

            _log.LogInformation(
                "Extension witness connected (connection {Connection}, {Open} open).", connection, open);
            Raise(ConnectionOpened, connection, nameof(ConnectionOpened));
            if (first)
                RaiseConnectionChanged(true);
        }
    }

    private void OnDisconnected(object? sender, WitnessConnection connection)
    {
        lock (_announceGate)
        {
            int open;
            lock (_gate)
            {
                // Only a genuine connected→disconnected transition is a drop. A
                // disconnect we never saw connect (a failed accept, a stop) is not
                // the student disabling the extension.
                if (!_connections.Remove(connection.Id)) return;
                open = _connections.Count;
            }

            Raise(ConnectionClosed, connection, nameof(ConnectionClosed));

            if (open > 0)
            {
                // Another profile's extension is still connected (#517).
                _log.LogInformation(
                    "Extension witness connection {Connection} dropped; {Open} still open.", connection, open);
                return;
            }

            _log.LogInformation("Extension witness connection {Connection} dropped; none left.", connection);
            RaiseConnectionChanged(false);
        }
    }

    private void Raise(EventHandler<WitnessConnection>? handlers, WitnessConnection connection, string name)
    {
        try
        {
            handlers?.Invoke(this, connection);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "A witness {Event} subscriber threw (connection {Connection})", name, connection);
        }
    }

    private void RaiseConnectionChanged(bool connected)
    {
        try
        {
            ConnectionChanged?.Invoke(this, connected);
        }
        catch (Exception ex)
        {
            // A subscriber's failure must not break the witness loop.
            _log.LogWarning(ex, "A witness ConnectionChanged subscriber threw (connected={Connected})", connected);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _transport.WitnessConnected -= OnConnected;
        _transport.WitnessDisconnected -= OnDisconnected;
        await _transport.StopAsync().ConfigureAwait(false);
    }
}
