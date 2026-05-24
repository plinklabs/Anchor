using System.Threading;
using FocusAgent.Core.Logging;
using FocusAgent.Core.Watchdog;
using Serilog;

namespace FocusAgent.Watchdog;

public static class Program
{
    /// <summary>Override the App exe path resolution (dev convenience).</summary>
    public const string AppPathArg = "--app-path";

    /// <summary>Run a single tick, log the outcome, and exit. Used by the verify script.</summary>
    public const string OneShotArg = "--one-shot";

    public static async Task<int> Main(string[] args)
    {
        var logDir = AgentLogPaths.LocalAppDataLogDirectory();
        Directory.CreateDirectory(logDir);

        // Assign to the static Log.Logger so the finally block's
        // Log.CloseAndFlush() actually flushes THIS logger and not a
        // default no-op one — without that flush, log lines emitted in
        // --one-shot mode never reach disk before the process exits.
        var log = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                path: Path.Combine(logDir, "watchdog-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        Log.Logger = log;

        try
        {
            var appPathOverride = ParseValueAfter(args, AppPathArg);
            var oneShot = args.Any(a => string.Equals(a, OneShotArg, StringComparison.OrdinalIgnoreCase));

            // Single-instance: avoid the StartupTask + a manual launch
            // double-firing the supervisor and causing duplicate relaunches.
            using var ownLock = new Mutex(initiallyOwned: false, AnchorWatchdogPaths.WatchdogPresenceMutexName, out _);
            if (!ownLock.WaitOne(0))
            {
                log.Information("Another watchdog instance is already running — exiting");
                return 0;
            }

            try
            {
                var probe = new MutexAppPresenceProbe();
                var launcher = new AppLauncher(log, appPathOverride);
                var policy = WatchdogPolicy.Default;
                var host = new WatchdogHost(
                    probe,
                    launcher,
                    policy,
                    TimeProvider.System,
                    log,
                    QuitFlagMtime);

                if (oneShot)
                {
                    var outcome = host.Tick();
                    log.Information("--one-shot tick outcome: {Outcome}", outcome.ToString());
                    return 0;
                }

                // No CancelKeyPress hook: the Watchdog is a WinExe (no
                // console attached) launched as an MSIX StartupTask, so
                // Ctrl+C is never a thing. Windows tears it down at
                // sign-out / uninstall by terminating the process; we don't
                // need a cooperative cancellation path.
                await host.RunAsync(CancellationToken.None).ConfigureAwait(false);
                return 0;
            }
            finally
            {
                try { ownLock.ReleaseMutex(); } catch { /* not owned -> ignore */ }
            }
        }
        catch (Exception ex)
        {
            log.Fatal(ex, "Watchdog terminated unexpectedly");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static DateTimeOffset? QuitFlagMtime()
    {
        var path = AnchorWatchdogPaths.QuitFlagPath();
        if (!File.Exists(path)) return null;
        try
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch
        {
            return null;
        }
    }

    private static string? ParseValueAfter(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }
}
