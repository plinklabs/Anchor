using FocusAgent.App.Connectivity;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace FocusAgent.App;

public sealed partial class MainWindow : Window
{
    private readonly ConnectionManager _connection;
    private readonly Action _onQuit;
    private readonly DispatcherQueue _dispatcher;

    public MainWindow(ConnectionManager connection, Action onQuit)
    {
        InitializeComponent();
        _connection = connection;
        _onQuit = onQuit;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Title = "FocusAgent";

        ApplySnapshot(_connection.Snapshot);
        _connection.StatusChanged += OnConnectionStatusChanged;
        Closed += OnClosed;
    }

    private void OnConnectionStatusChanged(object? sender, ConnectionStatusSnapshot snapshot) =>
        _dispatcher.TryEnqueue(() => ApplySnapshot(snapshot));

    private void ApplySnapshot(ConnectionStatusSnapshot s)
    {
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
                DetailText.Text = "Waiting for a focus session from your teacher.";
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

    private void OnQuitClicked(object sender, RoutedEventArgs e) => _onQuit();

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _connection.StatusChanged -= OnConnectionStatusChanged;
    }
}
