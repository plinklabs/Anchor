using FocusAgent.App.Localization;
using FocusAgent.Core.Sessions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace FocusAgent.App.Sessions;

public sealed partial class JoinConfirmationWindow : Window
{
    private readonly JoinConfirmation _confirmation;
    private readonly DispatcherQueue _dispatcher;

    public JoinConfirmationWindow(JoinConfirmation confirmation)
    {
        InitializeComponent();
        _confirmation = confirmation;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Title = Loc.Get("Title_FocusSession");

        TitleText.Text = Loc.Get("ConfirmTitle");
        CancelButton.Content = Loc.Get("ConfirmCancel");
        TeacherText.Text = Loc.Format("Confirm_Teacher", confirmation.TeacherDisplayName);
        UpdateCountdown(confirmation.Duration);

        confirmation.Tick += OnTick;
        confirmation.Finished += OnFinished;
        Closed += OnClosed;
    }

    private void OnTick(object? sender, TimeSpan remaining) =>
        _dispatcher.TryEnqueue(() => UpdateCountdown(remaining));

    private void OnFinished(object? sender, JoinDecision decision) =>
        _dispatcher.TryEnqueue(Close);

    private void UpdateCountdown(TimeSpan remaining)
    {
        var seconds = (int)Math.Ceiling(remaining.TotalSeconds);
        if (seconds < 0) seconds = 0;
        CountdownText.Text = Loc.Format("Confirm_Countdown", seconds);
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => _confirmation.Cancel();

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _confirmation.Tick -= OnTick;
        _confirmation.Finished -= OnFinished;
    }
}
