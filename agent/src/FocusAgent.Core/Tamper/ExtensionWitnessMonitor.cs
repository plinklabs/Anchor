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
/// </summary>
public sealed class ExtensionWitnessMonitor : IAsyncDisposable
{
    private readonly IExtensionWitnessTransport _transport;
    private readonly ITamperReporter _reporter;
    private readonly Func<Guid?> _activeSession;
    private readonly ILogger<ExtensionWitnessMonitor> _log;
    private readonly object _gate = new();

    private bool _connected;
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
    /// True while the extension's witness host is connected — i.e. the extension
    /// is installed and running. <see cref="FocusAgent.Core.Extension.ExtensionSelfRegistrar"/>
    /// reads this as the success signal for the force-install policy (#211): if it
    /// stays false past the grace period, the policy didn't take and the guided
    /// install is shown.
    /// </summary>
    public bool IsConnected
    {
        get { lock (_gate) return _connected; }
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
    /// the witness comes up, false when it drops. Raised on the transport's thread,
    /// in or out of a session.
    /// </summary>
    public event EventHandler<bool>? ConnectionChanged;

    private void OnConnected(object? sender, EventArgs e)
    {
        bool changed;
        lock (_gate)
        {
            changed = !_connected;
            _connected = true;
            _hasConnected = true;
        }
        _log.LogInformation("Extension witness connected.");
        if (changed)
            RaiseConnectionChanged(true);
    }

    private void OnDisconnected(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            // Only a genuine connected→disconnected transition is a drop. A
            // disconnect we never saw connect (a failed accept, a stop) is not
            // the student disabling the extension.
            if (!_connected) return;
            _connected = false;
        }

        RaiseConnectionChanged(false);

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
