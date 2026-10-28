using FocusAgent.App.Localization;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace FocusAgent.App.Extension;

/// <summary>
/// The agent's own explanation before the one UAC prompt of the Edge extension
/// setup (#405): what Windows is about to ask, and what Edge will say afterwards
/// ("managed by your organization"), in words a student or a parent can follow.
/// "Allow" goes on to the UAC prompt; "Not now" or closing the window falls back
/// to the guided install, and the agent stops asking at every start (the tray menu
/// offers it again).
/// </summary>
public sealed partial class ExtensionPolicyConsentWindow : Window
{
    private readonly TaskCompletionSource<bool?> _answer =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ExtensionPolicyConsentWindow()
    {
        InitializeComponent();
        Title = Loc.Get("Title_ExtensionConsent");
        WindowIcon.Apply(this);
        HeadlineText.Text = Loc.Get("ConsentHeadline");
        BodyText.Text = Loc.Get("ConsentBody");
        NoteText.Text = Loc.Get("ConsentManagedNote");
        NotNowButton.Content = Loc.Get("ConsentNotNow");
        AllowButton.Content = Loc.Get("ConsentAllow");

        // The student closing the window (title-bar X, Alt+F4) is a "not now".
        // AppWindow.Closing only fires for such a system close, not when the agent
        // itself closes the window on its way out; that leaves no answer (Closed).
        var appWindow = AppWindow.GetFromWindowId(
            Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this)));
        appWindow.Closing += (_, _) => _answer.TrySetResult(false);
        Closed += (_, _) => _answer.TrySetResult(null);
    }

    /// <summary>
    /// True once the student chose Allow; false for Not now or closing the window;
    /// null when the window was closed without an answer (the agent shutting down).
    /// </summary>
    public Task<bool?> Answer => _answer.Task;

    /// <summary>
    /// Answer as if the student clicked one of the buttons, and close. Used by the
    /// buttons and by the dev-only status endpoint control the agent e2e drives
    /// (the headless stand-in for the click, like <c>POST /leave</c>). UI thread only.
    /// </summary>
    internal void Choose(bool allow)
    {
        _answer.TrySetResult(allow);
        Close();
    }

    private void OnAllowClicked(object sender, RoutedEventArgs e) => Choose(allow: true);

    private void OnNotNowClicked(object sender, RoutedEventArgs e) => Choose(allow: false);
}
