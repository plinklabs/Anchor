using FocusAgent.App.Connectivity;
using FocusAgent.Core.Dtos;
using FocusAgent.Core.Sessions;
using FocusAgent.Core.Settings;
using Microsoft.Extensions.Options;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace FocusAgent.App;

public sealed partial class MainWindow : Window
{
    private static readonly SolidColorBrush FreshBrush = new(Colors.LimeGreen);
    private static readonly SolidColorBrush StaleBrush = new(Colors.OrangeRed);
    private static readonly SolidColorBrush IdleBrush = new(Colors.Gray);

    private readonly ConnectionManager _connection;
    private readonly SessionCoordinator _coordinator;
    private readonly SessionHeartbeatService _heartbeat;
    private readonly TimeSpan _heartbeatInterval;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _freshnessTimer;
    private readonly AppWindow _appWindow;

    private ConnectionStatusSnapshot _connectionSnapshot;
    private Guid? _joinedSessionId;
    private DateTimeOffset? _sessionStartedAt;
    private DateTimeOffset? _lastPingAt;
    // Set just before a real shutdown (tray → Exit) so the close-to-tray
    // interception below steps aside and lets the window actually close.
    private bool _exiting;

    public MainWindow(
        ConnectionManager connection,
        SessionCoordinator coordinator,
        SessionHeartbeatService heartbeat,
        IOptions<SessionSettings> sessionSettings)
    {
        InitializeComponent();
        _connection = connection;
        _coordinator = coordinator;
        _heartbeat = heartbeat;
        _heartbeatInterval = TimeSpan.FromSeconds(Math.Max(1, sessionSettings.Value.HeartbeatIntervalSeconds));
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Title = "FocusAgent";

        // #102: closing the window only hides it to the tray — the agent keeps
        // running and the heartbeat continues. Intercept the title-bar X here
        // and route it through the same hide path as the Close button.
        var hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        _appWindow.Closing += OnAppWindowClosing;

        _connectionSnapshot = _connection.Snapshot;

        // Repaints the "last ping HH:MM:SS (stale)" label without waiting for
        // another Pinged event — needed so the freshness indicator can flip to
        // red on its own after >2× interval of silence.
        _freshnessTimer = _dispatcher.CreateTimer();
        _freshnessTimer.Interval = TimeSpan.FromSeconds(1);
        _freshnessTimer.Tick += (_, _) => UpdateFreshnessLabel();

        RenderAll();

        _connection.StatusChanged += OnConnectionStatusChanged;
        _coordinator.SessionJoined += OnSessionJoined;
        _coordinator.SessionLeft += OnSessionLeft;
        _heartbeat.Pinged += OnHeartbeatPinged;
        Closed += OnClosed;
    }

    private void OnConnectionStatusChanged(object? sender, ConnectionStatusSnapshot snapshot) =>
        _dispatcher.TryEnqueue(() =>
        {
            _connectionSnapshot = snapshot;
            RenderAll();
        });

    private void OnSessionJoined(object? sender, SessionStartedPayload payload) =>
        _dispatcher.TryEnqueue(() =>
        {
            _joinedSessionId = payload.SessionId;
            _sessionStartedAt = payload.StartedAt;
            _lastPingAt = null;
            _freshnessTimer.Start();
            RenderAll();
        });

    private void OnSessionLeft(object? sender, Guid sessionId) =>
        _dispatcher.TryEnqueue(() =>
        {
            if (_joinedSessionId != sessionId) return;
            _joinedSessionId = null;
            _sessionStartedAt = null;
            _lastPingAt = null;
            _freshnessTimer.Stop();
            RenderAll();
        });

    private void OnHeartbeatPinged(object? sender, DateTimeOffset at) =>
        _dispatcher.TryEnqueue(() =>
        {
            _lastPingAt = at;
            UpdateFreshnessLabel();
        });

    private void RenderAll()
    {
        ApplyConnectionSnapshot();
        ApplySessionState();
    }

    private void ApplyConnectionSnapshot()
    {
        var s = _connectionSnapshot;
        // Status line is the headline. Detail is the supporting explanation
        // (last error, or invitation to act). Primary button label/enabled
        // reflects the next available action — the user should never have to
        // wonder "what do I do now?".
        switch (s.Status)
        {
            case ConnectionStatus.Idle:
                StatusText.Text = "Starting…";
                DetailText.Text = "";
                PrimaryButton.Content = "Sign in";
                PrimaryButton.IsEnabled = false;
                break;
            case ConnectionStatus.SigningIn:
                StatusText.Text = "Signing in…";
                DetailText.Text = "If a Windows account picker appears, choose your school account.";
                PrimaryButton.Content = "Sign in";
                PrimaryButton.IsEnabled = false;
                break;
            case ConnectionStatus.Connecting:
                StatusText.Text = "Connecting…";
                DetailText.Text = "";
                PrimaryButton.Content = "Reconnect";
                PrimaryButton.IsEnabled = false;
                break;
            case ConnectionStatus.Reconnecting:
                StatusText.Text = "Reconnecting…";
                DetailText.Text = s.LastError ?? "";
                PrimaryButton.Content = "Reconnect";
                PrimaryButton.IsEnabled = false;
                break;
            case ConnectionStatus.Connected:
                StatusText.Text = string.IsNullOrWhiteSpace(s.DisplayName)
                    ? "Connected"
                    : $"Connected as {s.DisplayName}";
                // The "waiting for a session" line is misleading once we're
                // actually in one — the session panel below carries the truth.
                DetailText.Text = _joinedSessionId is null
                    ? "Waiting for a focus session from your teacher."
                    : "";
                PrimaryButton.Content = "Reconnect";
                PrimaryButton.IsEnabled = true;
                break;
            case ConnectionStatus.Disconnected:
                StatusText.Text = "Disconnected";
                DetailText.Text = s.LastError ?? "Retrying automatically.";
                PrimaryButton.Content = "Reconnect now";
                PrimaryButton.IsEnabled = true;
                break;
            case ConnectionStatus.SignInFailed:
                StatusText.Text = "Not signed in";
                DetailText.Text = s.LastError ?? "Click Sign in to try again.";
                PrimaryButton.Content = "Sign in";
                PrimaryButton.IsEnabled = true;
                break;
        }
    }

    private void ApplySessionState()
    {
        if (_joinedSessionId is null)
        {
            SessionPanel.Visibility = Visibility.Collapsed;
            // "Leave session" only makes sense while actually in one (#102).
            LeaveButton.Visibility = Visibility.Collapsed;
            return;
        }
        SessionPanel.Visibility = Visibility.Visible;
        LeaveButton.Visibility = Visibility.Visible;
        SessionStatusText.Text = _sessionStartedAt is { } started
            ? $"In session since {started.ToLocalTime():HH:mm}"
            : "In session";
        UpdateFreshnessLabel();
    }

    private void UpdateFreshnessLabel()
    {
        if (_joinedSessionId is null) return;
        if (_lastPingAt is not DateTimeOffset last)
        {
            HeartbeatDot.Fill = IdleBrush;
            HeartbeatText.Text = "Waiting for first heartbeat…";
            return;
        }
        // Backend's HeartbeatMonitor calls the agent stale at 2× the interval;
        // mirror that here so the UI signal matches the server's view.
        var stale = (DateTimeOffset.UtcNow - last) > TimeSpan.FromTicks(_heartbeatInterval.Ticks * 2);
        HeartbeatDot.Fill = stale ? StaleBrush : FreshBrush;
        var label = $"Last ping {last.ToLocalTime():HH:mm:ss}";
        HeartbeatText.Text = stale ? $"{label} (stale)" : label;
    }

    private async void OnPrimaryClicked(object sender, RoutedEventArgs e)
    {
        PrimaryButton.IsEnabled = false;
        try
        {
            await _connection.RetryAsync();
        }
        catch
        {
            // RetryAsync surfaces failure via StatusChanged; nothing to do here.
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => HideToTray();

    private async void OnLeaveClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Leave session?",
            Content = "Leaving will tell your teacher you left the session. Continue?",
            PrimaryButtonText = "Leave session",
            CloseButtonText = "Stay",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        LeaveButton.IsEnabled = false;
        try
        {
            await _coordinator.LeaveSessionManuallyAsync();
        }
        catch
        {
            // LeaveSessionManuallyAsync is best-effort and logs its own failures;
            // SessionLeft still clears the panel, so there's nothing to do here.
        }
        finally
        {
            LeaveButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Hide the window to the tray without ending the session or stopping the
    /// agent (#102). Re-opened via the tray's "Open" item — see
    /// <see cref="ShowFromTray"/>.
    /// </summary>
    public void HideToTray() => _appWindow.Hide();

    /// <summary>Re-show the window after it was hidden to the tray.</summary>
    public void ShowFromTray()
    {
        _appWindow.Show();
        Activate();
    }

    /// <summary>
    /// Allow the next close to actually tear the window down instead of hiding
    /// it. Called by the app right before <c>Application.Exit</c> (tray → Exit).
    /// </summary>
    public void AllowClose() => _exiting = true;

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting) return; // genuine shutdown — let the window close.
        args.Cancel = true;
        sender.Hide();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _appWindow.Closing -= OnAppWindowClosing;
        _connection.StatusChanged -= OnConnectionStatusChanged;
        _coordinator.SessionJoined -= OnSessionJoined;
        _coordinator.SessionLeft -= OnSessionLeft;
        _heartbeat.Pinged -= OnHeartbeatPinged;
        _freshnessTimer.Stop();
    }
}
