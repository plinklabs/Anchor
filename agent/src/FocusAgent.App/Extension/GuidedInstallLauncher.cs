using FocusAgent.App.Sessions;
using FocusAgent.Core.Extension;
using Microsoft.UI.Dispatching;

namespace FocusAgent.App.Extension;

/// <summary>
/// Shows the <see cref="GuidedInstallWindow"/> on the UI thread and completes when
/// the student dismisses it (#211). The registrar's fallback callback runs on a
/// background task, so window creation must be marshalled onto the WinUI
/// dispatcher — this is the bridge that does it, mirroring how
/// <see cref="JoinByCodeFlow"/> opens the join dialog.
/// </summary>
public sealed class GuidedInstallLauncher
{
    private readonly DispatcherQueue _dispatcher;
    private readonly IStoreLauncher _storeLauncher;

    public GuidedInstallLauncher(DispatcherQueue dispatcher, IStoreLauncher storeLauncher)
    {
        _dispatcher = dispatcher;
        _storeLauncher = storeLauncher;
    }

    /// <summary>
    /// Open the guided-install window and await its dismissal. Safe to call from a
    /// background thread; the window is created and shown on the UI thread.
    /// </summary>
    public Task ShowAsync(CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var enqueued = _dispatcher.TryEnqueue(() =>
        {
            try
            {
                var window = new GuidedInstallWindow(_storeLauncher);
                DialogWindowPositioner.ConfigureAndShow(window);
                _ = window.Completion.ContinueWith(_ => tcs.TrySetResult(), TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        if (!enqueued)
            tcs.TrySetResult(); // dispatcher gone (shutting down) — nothing to show.

        return tcs.Task;
    }
}
