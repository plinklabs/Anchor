using FocusAgent.App.Localization;
using FocusAgent.Core.Sessions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;

namespace FocusAgent.App.Sessions;

public sealed partial class JoinByCodeWindow : Window
{
    private readonly IJoinByCodeClient _client;
    private readonly DispatcherQueue _dispatcher;
    private readonly TaskCompletionSource<JoinByCodeStatus?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _busy;

    public JoinByCodeWindow(IJoinByCodeClient client)
    {
        InitializeComponent();
        _client = client;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Title = Loc.Get("Title_JoinByCode");
        HeadlineText.Text = Loc.Get("JoinHeadline");
        BodyText.Text = Loc.Get("JoinBody");
        CancelButton.Content = Loc.Get("JoinCancel");
        JoinButton.Content = Loc.Get("Join_Button_Join");

        Closed += OnClosed;
        Activated += OnFirstActivated;
    }

    /// <summary>
    /// Completes with the terminal status — <see cref="JoinByCodeStatus.Success"/>
    /// for a successful join, or <c>null</c> if the user cancelled / closed
    /// without joining. Errors are shown inline and the window stays open so
    /// the student can retry with a different code; they don't surface here.
    /// </summary>
    public Task<JoinByCodeStatus?> Completion => _completion.Task;

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;
        CodeBox.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Dev-only (#175 / <c>--show-test-joinbycode</c>): pre-fill a valid 6-digit
    /// code so the dialog renders its "ready to join" state — the JOIN button
    /// enabled at full magenta — for the visual e2e to capture. Setting the text
    /// drives the same <see cref="OnCodeTextChanged"/> path a real keystroke would,
    /// so JOIN enables exactly as in production. See App.RunJoinByCodeSelfTest.
    /// </summary>
    internal void PrefillForSelfTest()
    {
        CodeBox.Text = "123456";
        // Drive JOIN to its enabled (full-magenta) state directly: a programmatic
        // Text set before the control tree is fully loaded doesn't reliably leave
        // the button in its Normal visual state, and the self-test wants the spark
        // rendered, not the dimmed disabled fill.
        JoinButton.IsEnabled = true;
    }

    private void OnCodeBeforeTextChanging(TextBox sender, TextBoxBeforeTextChangingEventArgs args)
    {
        // The TextBox already enforces MaxLength; we additionally reject any
        // non-digit so paste-from-clipboard never sneaks letters in.
        foreach (var ch in args.NewText)
        {
            if (!char.IsDigit(ch))
            {
                args.Cancel = true;
                return;
            }
        }
    }

    private void OnCodeTextChanged(object sender, TextChangedEventArgs e)
    {
        var ready = CodeBox.Text.Length == 6 && !_busy;
        JoinButton.IsEnabled = ready;
        // Wipe any previous error the moment the student edits the field,
        // so it doesn't look like a fresh result of their latest keystroke.
        HideMessage();
    }

    private async void OnCodeKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && JoinButton.IsEnabled)
        {
            e.Handled = true;
            await AttemptJoinAsync();
        }
    }

    private async void OnJoinClicked(object sender, RoutedEventArgs e) => await AttemptJoinAsync();

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        _completion.TrySetResult(null);
        Close();
    }

    private async Task AttemptJoinAsync()
    {
        if (_busy) return;
        var code = CodeBox.Text.Trim();
        if (code.Length != 6) return;

        SetBusy(true);
        JoinByCodeOutcome outcome;
        try
        {
            outcome = await _client.JoinAsync(code).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            outcome = new JoinByCodeOutcome(JoinByCodeStatus.NetworkError, ex.Message);
        }

        if (outcome.Status == JoinByCodeStatus.Success)
        {
            _completion.TrySetResult(JoinByCodeStatus.Success);
            _dispatcher.TryEnqueue(Close);
            return;
        }

        SetBusy(false);
        ShowMessage(outcome.Message);

        // On rate-limit, disable Join to make it visually clear that hammering
        // the button won't help — the student must wait.
        if (outcome.Status == JoinByCodeStatus.RateLimited)
            JoinButton.IsEnabled = false;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        CodeBox.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        JoinButton.IsEnabled = !busy && CodeBox.Text.Length == 6;
        JoinButton.Content = busy ? Loc.Get("Join_Button_Joining") : Loc.Get("Join_Button_Join");
    }

    private void ShowMessage(string text)
    {
        MessageText.Text = text;
        MessageText.Visibility = Visibility.Visible;
    }

    private void HideMessage()
    {
        MessageText.Text = "";
        MessageText.Visibility = Visibility.Collapsed;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        // If the window closes without a definitive result, treat it as cancel
        // so the orchestrator's await unblocks.
        _completion.TrySetResult(null);
    }
}
