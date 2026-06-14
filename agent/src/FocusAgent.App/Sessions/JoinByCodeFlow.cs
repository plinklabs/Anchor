using FocusAgent.Core.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;

namespace FocusAgent.App.Sessions;

/// <summary>
/// Owns the single-instance lifetime of the join-by-code dialog. The tray
/// menu calls <see cref="Open"/>; clicking it again while the dialog is
/// already up just refocuses the existing window instead of stacking
/// duplicates.
///
/// The actual REST call lives inside <see cref="JoinByCodeWindow"/> (it
/// drives Busy state, inline error messages, and the retry loop); this
/// class is purely the host glue.
/// </summary>
public sealed class JoinByCodeFlow
{
    private readonly IJoinByCodeClient _client;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger<JoinByCodeFlow> _log;
    private readonly object _gate = new();
    private JoinByCodeWindow? _current;

    public JoinByCodeFlow(
        IJoinByCodeClient client,
        DispatcherQueue dispatcher,
        ILogger<JoinByCodeFlow>? log = null)
    {
        _client = client;
        _dispatcher = dispatcher;
        _log = log ?? NullLogger<JoinByCodeFlow>.Instance;
    }

    /// <summary>True while the dialog is on screen.</summary>
    public bool IsOpen
    {
        get { lock (_gate) return _current is not null; }
    }

    public void Open()
    {
        _dispatcher.TryEnqueue(() =>
        {
            JoinByCodeWindow window;
            lock (_gate)
            {
                if (_current is not null)
                {
                    // Already up — refocus instead of stacking another window.
                    try { _current.Activate(); }
                    catch (Exception ex) { _log.LogDebug(ex, "Activate on existing join-by-code window threw."); }
                    return;
                }
                window = new JoinByCodeWindow(_client);
                _current = window;
            }

            DialogWindowPositioner.ConfigureAndShow(window);

            // Detach when the user dismisses it (cancel / close / success).
            _ = window.Completion.ContinueWith(t =>
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_current, window))
                        _current = null;
                }
                _log.LogInformation("Join-by-code dialog closed with result {Result}.", t.Result?.ToString() ?? "Cancelled");
            }, TaskScheduler.Default);
        });
    }
}
