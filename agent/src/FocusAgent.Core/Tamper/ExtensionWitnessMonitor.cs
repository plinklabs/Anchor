using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusAgent.Core.Tamper;

/// <summary>
/// Turns the extension witness link's drop into a tamper report (#146 part 1).
/// The extension cannot witness its own disablement/removal, so the agent acts
/// as the on-box witness: when the native-messaging link drops while a session
/// is joined, the student disabled/removed the extension (or closed the browser)
/// mid-lesson, which we surface as <see cref="TamperKinds.ExtensionDisabled"/>.
///
/// Reporting is gated on an active <em>joined</em> session — the hub rejects
/// events from a non-participant, and a drop outside a session is just the
/// student closing their browser, not tampering. The report makes the drop
/// visible to the teacher; <see cref="ExtensionGuard"/> listens to
/// <see cref="ConnectionChanged"/> and pauses Edge while the extension stays
/// gone (#427).
///
/// Every Edge profile has its own copy of the extension and its own link (#517),
/// so the monitor keeps the set of open <see cref="Connections"/> and raises
/// <see cref="ConnectionOpened"/> / <see cref="ConnectionClosed"/> for each one.
/// "The extension is there" (<see cref="IsConnected"/>,
/// <see cref="ConnectionChanged"/>) means at least one of them is connected, and
/// for now only the last one dropping is reported: one profile's link closing
/// while another is still up is usually that profile's last window being closed,
/// which the pipe alone can't tell from the extension being switched off there.
/// </summary>
public sealed class ExtensionWitnessMonitor : IAsyncDisposable
{
    private readonly IExtensionWitnessTransport _transport;
    private readonly ITamperReporter _reporter;
    private readonly Func<Guid?> _activeSession;
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
    /// <param name="reporter">Sends the TamperDetected event.</param>
    /// <param name="activeSession">
    /// Returns the joined session id, or null when not in a session. Wire to
    /// <c>SessionCoordinator.JoinedSessionId</c>: only a joined session can carry
    /// a participant-scoped event, and only then is a drop tampering.
    /// </param>
    public ExtensionWitnessMonitor(
        IExtensionWitnessTransport transport,
        ITamperReporter reporter,
        Func<Guid?> activeSession,
        ILogger<ExtensionWitnessMonitor>? log = null)
    {
        _transport = transport;
        _reporter = reporter;
        _activeSession = activeSession;
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
                // Another profile's extension is still connected (#517). Most
                // often this is a profile's last window being closed.
                _log.LogInformation(
                    "Extension witness connection {Connection} dropped; {Open} still open — not reporting.",
                    connection, open);
                return;
            }

            RaiseConnectionChanged(false);
        }

        var sessionId = _activeSession();
        if (sessionId is null)
        {
            _log.LogDebug("Extension witness dropped outside a session — not reporting.");
            return;
        }

        _log.LogWarning(
            "Extension witness dropped during session {SessionId} — reporting {Kind}.",
            sessionId, TamperKinds.ExtensionDisabled);
        _ = ReportAsync(sessionId.Value);
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
            // A subscriber's failure must not break the witness loop or skip the
            // tamper report below.
            _log.LogWarning(ex, "A witness ConnectionChanged subscriber threw (connected={Connected})", connected);
        }
    }

    private async Task ReportAsync(Guid sessionId)
    {
        try
        {
            await _reporter.ReportAsync(sessionId, TamperKinds.ExtensionDisabled).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort like every other tamper report: a failed send must not
            // crash the witness loop. The dashboard still surfaces it on reload
            // if the event reached the backend before the transport hiccup.
            _log.LogWarning(ex, "Reporting {Kind} failed for session {SessionId}",
                TamperKinds.ExtensionDisabled, sessionId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _transport.WitnessConnected -= OnConnected;
        _transport.WitnessDisconnected -= OnDisconnected;
        await _transport.StopAsync().ConfigureAwait(false);
    }
}
