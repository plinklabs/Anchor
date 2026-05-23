using FocusAgent.App.Auth;
using FocusAgent.App.Focus;
using FocusAgent.App.Realtime;
using FocusAgent.App.Sessions;
using FocusAgent.App.Tray;
using FocusAgent.Core.Auth;
using FocusAgent.Core.Focus;
using FocusAgent.Core.Logging;
using FocusAgent.Core.Realtime;
using FocusAgent.Core.Sessions;
using FocusAgent.Core.Settings;
using FocusAgent.Native;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Serilog;
using WinRT.Interop;

namespace FocusAgent.App;

public partial class App : Application
{
    private IHost? _host;
    private MainWindow? _mainWindow;
    private TrayIconHost? _tray;
    private SessionCoordinator? _coordinator;
    private FocusSessionController? _focus;
    private ISessionHubConnection? _hub;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            _host = BuildHost(dispatcher, () => _mainWindow is null ? IntPtr.Zero : WindowNative.GetWindowHandle(_mainWindow));
            _host.Start();

            var logger = _host.Services.GetRequiredService<ILogger<App>>();
            logger.LogInformation("FocusAgent starting (unpackaged WinUI 3)");

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                logger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception");
            TaskScheduler.UnobservedTaskException += (_, e) =>
                logger.LogError(e.Exception, "Unobserved task exception");

            _mainWindow = new MainWindow();
            _tray = new TrayIconHost(
                onOpen: () => _mainWindow.Activate(),
                onQuit: () => Exit(),
                dispatcher: dispatcher);
            _tray.Show();

            _hub = _host.Services.GetRequiredService<ISessionHubConnection>();
            _coordinator = _host.Services.GetRequiredService<SessionCoordinator>();
            _focus = _host.Services.GetRequiredService<FocusSessionController>();

            _hub.StateChanged += (_, state) => _tray.UpdateStatus(state, LastDisplayName);
            _ = StartHubAsync(_host.Services, logger);
        }
        catch (Exception ex)
        {
            WriteStartupFailure(ex);
            throw;
        }
    }

    private string? LastDisplayName { get; set; }

    private async Task StartHubAsync(IServiceProvider services, ILogger<App> logger)
    {
        try
        {
            var tokens = services.GetRequiredService<IAuthTokenProvider>();
            var auth = await tokens.AcquireTokenAsync().ConfigureAwait(true);
            LastDisplayName = auth.DisplayName;
            _tray?.UpdateStatus(AgentConnectionState.Connecting, LastDisplayName);
            await _hub!.StartAsync().ConfigureAwait(true);
            _tray?.UpdateStatus(AgentConnectionState.Connected, LastDisplayName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start hub connection");
            _tray?.UpdateStatus(AgentConnectionState.Disconnected, LastDisplayName);
        }
    }

    private static void WriteStartupFailure(Exception ex)
    {
        try
        {
            var dir = AgentLogPaths.LocalAppDataLogDirectory();
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "startup-error.log"),
                $"{DateTimeOffset.Now:O}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // last-resort logger; intentionally swallow
        }
    }

    private static IHost BuildHost(DispatcherQueue dispatcher, Func<IntPtr> windowHandleProvider)
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true);

        builder.Services.AddOptions<BackendSettings>()
            .Bind(builder.Configuration.GetSection(BackendSettings.SectionName));
        builder.Services.AddOptions<AuthSettings>()
            .Bind(builder.Configuration.GetSection(AuthSettings.SectionName));
        builder.Services.AddOptions<RealtimeSettings>()
            .Bind(builder.Configuration.GetSection(RealtimeSettings.SectionName));
        builder.Services.AddOptions<SessionSettings>()
            .Bind(builder.Configuration.GetSection(SessionSettings.SectionName));

        builder.Services.AddSingleton(dispatcher);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<Func<IntPtr>>(_ => windowHandleProvider);
        // Capture the UI thread's SynchronizationContext so the foreground
        // watcher can marshal native callbacks onto it.
        builder.Services.AddSingleton(SynchronizationContext.Current
            ?? new DispatcherQueueSynchronizationContext(dispatcher));

        builder.Services.AddSingleton<IAuthTokenProvider, WamTokenProvider>();
        builder.Services.AddSingleton<ISessionHubConnection, SignalRSessionHubConnection>();
        builder.Services.AddSingleton<ISessionUiHost, WinUiSessionUiHost>();
        builder.Services.AddSingleton<SessionCoordinator>();

        builder.Services.AddSingleton<IAppIdentifier, AppIdentifier>();
        builder.Services.AddSingleton<IForegroundWatcher, ForegroundWatcher>();
        builder.Services.AddSingleton<IFocusEnforcer, FocusEnforcer>();
        builder.Services.AddSingleton<IFocusEventReporter, SignalRFocusEventReporter>();
        builder.Services.AddSingleton<FocusSessionController>();

        var logDir = AgentLogPaths.LocalAppDataLogDirectory();
        Directory.CreateDirectory(logDir);

        var serilog = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                path: Path.Combine(logDir, "focusagent-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(serilog, dispose: true);
        builder.Logging.AddDebug();

        return builder.Build();
    }
}
