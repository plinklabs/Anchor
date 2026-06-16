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
using PlinkDesignSystem.Controls;
using WinRT.Interop;

namespace FocusAgent.App;

public sealed partial class MainWindow : Window
{
    private readonly ConnectionManager? _connection;
    private readonly SessionCoordinator? _coordinator;
    private readonly SessionHeartbeatService? _heartbeat;
    private readonly TimeSpan _heartbeatInterval;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer? _freshnessTimer;
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
        : this(TimeSpan.FromSeconds(Math.Max(1, sessionSettings.Value.HeartbeatIntervalSeconds)))
    {
        _connection = connection;
        _coordinator = coordinator;
        _heartbeat = heartbeat;

        // Repaints the "last ping HH:MM:SS (stale)" label without waiting for
        // another Pinged event — needed so the freshness indicator can flip to
        // stale on its own after >2× interval of silence.
        _freshnessTimer = _dispatcher.CreateTimer();
        _freshnessTimer.Interval = TimeSpan.FromSeconds(1);
        _freshnessTimer.Tick += (_, _) => UpdateFreshnessLabel();

        _connectionSnapshot = _connection.Snapshot;
        RenderAll();

        _connection.StatusChanged += OnConnectionStatusChanged;
        _coordinator.SessionJoined += OnSessionJoined;
        _coordinator.SessionLeft += OnSessionLeft;
        _heartbeat.Pinged += OnHeartbeatPinged;
    }

    /// <summary>
    /// Shared construction: window chrome, close-to-tray interception, and the
    /// fields both the production and the self-test (<c>--show-test-mainwindow</c>,
    /// #173) constructors need. The service-backed events are wired only by the
    /// production constructor above; the self-test renders a static synthetic
    /// state instead.
    /// </summary>
    private MainWindow(TimeSpan heartbeatInterval)
    {
        InitializeComponent();
        _heartbeatInterval = heartbeatInterval;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Title = "Anchor";

        // #102: closing the window only hides it to the tray — the agent keeps
        // running and the heartbeat continues. Intercept the title-bar X here
        // and route it through the same hide path as the Close button.
        var hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        _appWindow.Closing += OnAppWindowClosing;

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
            _freshnessTimer?.Start();
            RenderAll();
        });

    private void OnSessionLeft(object? sender, Guid sessionId) =>
        _dispatcher.TryEnqueue(() =>
        {
            if (_joinedSessionId != sessionId) return;
            _joinedSessionId = null;
            _sessionStartedAt = null;
            _lastPingAt = null;
            _freshnessTimer?.Stop();
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
                PrimaryButton.Content = "SIGN IN";
                PrimaryButton.IsEnabled = false;
                break;
            case ConnectionStatus.SigningIn:
                StatusText.Text = "Signing in…";
                DetailText.Text = "If a Windows account picker appears, choose your school account.";
                PrimaryButton.Content = "SIGN IN";
                PrimaryButton.IsEnabled = false;
                break;
            case ConnectionStatus.Connecting:
                StatusText.Text = "Connecting…";
                DetailText.Text = "";
                PrimaryButton.Content = "RECONNECT";
                PrimaryButton.IsEnabled = false;
                break;
            case ConnectionStatus.Reconnecting:
                StatusText.Text = "Reconnecting…";
                DetailText.Text = s.LastError ?? "";
                PrimaryButton.Content = "RECONNECT";
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
                PrimaryButton.Content = "RECONNECT";
                PrimaryButton.IsEnabled = true;
                break;
            case ConnectionStatus.Disconnected:
                StatusText.Text = "Disconnected";
                DetailText.Text = s.LastError ?? "Retrying automatically.";
                PrimaryButton.Content = "RECONNECT NOW";
                PrimaryButton.IsEnabled = true;
                break;
            case ConnectionStatus.SignInFailed:
                StatusText.Text = "Not signed in";
                DetailText.Text = s.LastError ?? "Click Sign in to try again.";
                PrimaryButton.Content = "SIGN IN";
                PrimaryButton.IsEnabled = true;
                break;
        }

        // The connection ping carries liveness in the eyebrow: it pulses only
        // while we're actually Connected, and falls still otherwise (idle /
        // connecting / down) — the signature motif standing in for a status dot.
        ConnectionPing.Mode = s.Status == ConnectionStatus.Connected
            ? PingMode.Pulse
            : PingMode.Static;
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
            // No ping yet: a quiet, still ring while we wait for the first one.
            HeartbeatPing.Mode = PingMode.Static;
            HeartbeatText.Text = "Waiting for first heartbeat…";
            return;
        }
        // Backend's HeartbeatMonitor calls the agent stale at 2× the interval;
        // mirror that here so the UI signal matches the server's view. A fresh
        // heartbeat pulses the ping (a live pulse); a stale one falls still.
        var stale = (DateTimeOffset.UtcNow - last) > TimeSpan.FromTicks(_heartbeatInterval.Ticks * 2);
        HeartbeatPing.Mode = stale ? PingMode.Static : PingMode.Pulse;
        var label = $"Last ping {last.ToLocalTime():HH:mm:ss}";
        HeartbeatText.Text = stale ? $"{label} (stale)" : label;
    }

    private async void OnPrimaryClicked(object sender, RoutedEventArgs e)
    {
        if (_connection is null) return;
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
        if (_coordinator is null) return;

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
        if (_connection is not null) _connection.StatusChanged -= OnConnectionStatusChanged;
        if (_coordinator is not null) _coordinator.SessionJoined -= OnSessionJoined;
        if (_coordinator is not null) _coordinator.SessionLeft -= OnSessionLeft;
        if (_heartbeat is not null) _heartbeat.Pinged -= OnHeartbeatPinged;
        _freshnessTimer?.Stop();
    }

    /// <summary>
    /// Dev-only self-test entry (#173 / <c>--show-test-mainwindow</c>): build the
    /// real MainWindow with no host / WAM / hub / coordinator and render a
    /// representative "connected, in a focus session, heartbeat fresh" state, so
    /// the visual e2e can assert the redesigned ink surface actually paints. The
    /// rendering path is identical to production — only the *source* of the state
    /// is synthetic. See App.RunMainWindowSelfTest / scripts/dev/verify scripts.
    /// </summary>
    public static MainWindow CreateSelfTest()
    {
        var window = new MainWindow(TimeSpan.FromSeconds(10));
        window._connectionSnapshot = new ConnectionStatusSnapshot(
            ConnectionStatus.Connected, SelfTestDemoContent.StudentName, LastError: null);
        window._joinedSessionId = Guid.NewGuid();
        window._sessionStartedAt = DateTimeOffset.Now.AddMinutes(-12);
        window._lastPingAt = DateTimeOffset.UtcNow;
        window.RenderAll();
        return window;
    }
}
