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
/// Soft enforcement (design §5.4): we can't prevent it, only make it visible to
/// the teacher. Reporting is gated on an active <em>joined</em> session — the hub
/// rejects events from a non-participant, and a drop outside a session is just
/// the student closing their browser, not tampering.
/// </summary>
public sealed class ExtensionWitnessMonitor : IAsyncDisposable
{
    private readonly IExtensionWitnessTransport _transport;
    private readonly ITamperReporter _reporter;
    private readonly Func<Guid?> _activeSession;
    private readonly ILogger<ExtensionWitnessMonitor> _log;
    private readonly object _gate = new();

    private bool _connected;

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

    private void OnConnected(object? sender, EventArgs e)
    {
        lock (_gate) _connected = true;
        _log.LogInformation("Extension witness connected.");
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
