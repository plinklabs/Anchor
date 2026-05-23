using FocusAgent.Core.Sessions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace FocusAgent.App.Sessions;

public sealed class WinUiSessionUiHost : ISessionUiHost
{
    private readonly DispatcherQueue _dispatcher;
    private readonly object _gate = new();
    private JoinConfirmationWindow? _current;

    public WinUiSessionUiHost(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public Task<JoinDecision> ShowJoinConfirmationAsync(JoinConfirmation confirmation, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<JoinDecision>(TaskCreationOptions.RunContinuationsAsynchronously);

        confirmation.Finished += (_, decision) => tcs.TrySetResult(decision);
        ct.Register(() => confirmation.Abort());

        _dispatcher.TryEnqueue(() =>
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
        });

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
