using FocusAgent.Core.Extension;
using Microsoft.UI.Xaml;

namespace FocusAgent.App.Extension;

/// <summary>
/// The guided-install fallback window (#211): a small one-click prompt shown only
/// when the per-user force-install policy didn't take — the extension never
/// checked in over the witness link within the grace period. "Open store"
/// launches Edge at the canonical Add-ons listing where the student taps
/// <em>Get → Add</em>; "Later" dismisses it.
///
/// The actual launch is delegated to an injected <see cref="IStoreLauncher"/> so
/// the open-the-browser side effect is swappable (a no-op in headless verify, the
/// real shell-launch in production).
/// </summary>
public sealed partial class GuidedInstallWindow : Window
{
    private readonly IStoreLauncher _launcher;
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public GuidedInstallWindow(IStoreLauncher launcher)
    {
        InitializeComponent();
        _launcher = launcher;
        Title = "Anchor — Install the extension";
        Closed += OnClosed;
    }

    /// <summary>Completes when the window is dismissed (store opened or "Later").</summary>
    public Task Completion => _completion.Task;

    private void OnOpenStoreClicked(object sender, RoutedEventArgs e)
    {
        _launcher.OpenStoreListing(EdgeExtensionPolicy.StoreListingUrl);
        _completion.TrySetResult();
        Close();
    }

    private void OnLaterClicked(object sender, RoutedEventArgs e)
    {
        _completion.TrySetResult();
        Close();
    }

    private void OnClosed(object sender, WindowEventArgs args) => _completion.TrySetResult();
}
