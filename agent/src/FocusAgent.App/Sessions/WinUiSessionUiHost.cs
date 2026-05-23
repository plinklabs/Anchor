using FocusAgent.Core.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace FocusAgent.App.Sessions;

public sealed class WinUiSessionUiHost : ISessionUiHost
{
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger<WinUiSessionUiHost> _log;
    private readonly object _gate = new();
    private JoinConfirmationWindow? _current;

    public WinUiSessionUiHost(DispatcherQueue dispatcher, ILogger<WinUiSessionUiHost>? log = null)
    {
        _dispatcher = dispatcher;
        _log = log ?? NullLogger<WinUiSessionUiHost>.Instance;
    }

    public Task<JoinDecision> ShowJoinConfirmationAsync(JoinConfirmation confirmation, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<JoinDecision>(TaskCreationOptions.RunContinuationsAsynchronously);

        confirmation.Finished += (_, decision) => tcs.TrySetResult(decision);
        ct.Register(() => confirmation.Abort());

        _log.LogInformation(
            "Enqueuing join-confirmation toast for session {SessionId}",
            confirmation.Payload.SessionId);

        var enqueued = _dispatcher.TryEnqueue(() =>
        {
            try
            {
                JoinConfirmationWindow? previous;
                JoinConfirmationWindow current;
                lock (_gate)
                {
                    previous = _current;
                    current = new JoinConfirmationWindow(confirmation);
                    _current = current;
                }
                previous?.Close();
                ToastWindowPositioner.ConfigureAndShow(current);
                confirmation.Start();
                _log.LogInformation(
                    "Join-confirmation toast shown for session {SessionId}",
                    confirmation.Payload.SessionId);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "Failed to show join-confirmation toast for session {SessionId}",
                    confirmation.Payload.SessionId);
                confirmation.Abort();
            }
        });

        if (!enqueued)
        {
            _log.LogError(
                "DispatcherQueue rejected the join-confirmation toast for session {SessionId} — UI queue is shutting down. Aborting confirmation.",
                confirmation.Payload.SessionId);
            confirmation.Abort();
        }

        return tcs.Task;
    }

    public void DismissJoinConfirmation()
    {
        _dispatcher.TryEnqueue(() =>
        {
            JoinConfirmationWindow? window;
            lock (_gate)
            {
                window = _current;
                _current = null;
            }
            window?.Close();
        });
    }
}
