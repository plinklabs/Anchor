using FocusAgent.App.Sessions;
using Microsoft.UI.Dispatching;

namespace FocusAgent.App.Extension;

/// <summary>
/// Shows the <see cref="ExtensionPolicyConsentWindow"/> on the UI thread and
/// completes with the student's answer (#405). The registrar asks from a background
/// task, so window creation is marshalled onto the WinUI dispatcher, the same
/// bridge <see cref="GuidedInstallLauncher"/> uses. Keeps at most one window: the
/// tray action brings an open one to the front instead of stacking another.
/// </summary>
public sealed class ExtensionPolicyConsentLauncher
{
    private readonly DispatcherQueue _dispatcher;
    private readonly object _gate = new();
    private ExtensionPolicyConsentWindow? _current;

    public ExtensionPolicyConsentLauncher(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    /// <summary>True while the window is on screen.</summary>
    public bool IsOpen
    {
        get { lock (_gate) return _current is not null; }
    }

    /// <summary>
    /// Open the window and await the answer: true for Allow, false for Not now or
    /// the student closing it, null when it went away unanswered (the agent is
    /// shutting down). Safe to call from a background thread.
    /// </summary>
    public Task<bool?> AskAsync(CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));

        var enqueued = _dispatcher.TryEnqueue(() =>
        {
            try
            {
                var window = new ExtensionPolicyConsentWindow();
                lock (_gate) _current = window;
                _ = window.Answer.ContinueWith(t =>
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_current, window)) _current = null;
                    }
                    tcs.TrySetResult(t.Result);
                }, TaskScheduler.Default);
                DialogWindowPositioner.ConfigureAndShow(window, DialogWindowPositioner.TallMaxHeightDip);
            }
            catch (Exception ex)
            {
                lock (_gate) _current = null;
                tcs.TrySetException(ex);
            }
        });

        if (!enqueued)
            tcs.TrySetResult(null); // dispatcher gone (shutting down) — nobody to ask.

        return tcs.Task;
    }

    /// <summary>Bring the open window to the front; false when none is open.</summary>
    public bool TryActivate() => TryOnOpenWindow(w => w.Activate());

    /// <summary>
    /// Answer the open window as if a button was clicked (the dev-only status
    /// endpoint control). False when none is open.
    /// </summary>
    public bool TryAnswer(bool allow) => TryOnOpenWindow(w => w.Choose(allow));

    private bool TryOnOpenWindow(Action<ExtensionPolicyConsentWindow> action)
    {
        ExtensionPolicyConsentWindow? window;
        lock (_gate) window = _current;
        if (window is null) return false;
        return _dispatcher.TryEnqueue(() => action(window));
    }
}
