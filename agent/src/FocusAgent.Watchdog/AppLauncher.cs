using System.Diagnostics;
using Serilog;

namespace FocusAgent.Watchdog;

/// <summary>
/// Resolves the App exe path then shells out via <see cref="Process.Start(ProcessStartInfo)"/>.
/// Resolution order:
///   1. The path passed in the constructor (--app-path CLI flag for dev).
///   2. <c>Windows.ApplicationModel.Package.Current.InstalledLocation</c> + <c>FocusAgent.App.exe</c>
///      — succeeds when the Watchdog runs as part of the MSIX package.
///   3. <c>AppContext.BaseDirectory</c> + <c>FocusAgent.App.exe</c>
///      — a sibling-exe fallback for ad-hoc layouts.
/// </summary>
internal sealed class AppLauncher : IAppLauncher
{
    private readonly ILogger _log;
    private readonly string? _explicitAppPath;

    public AppLauncher(ILogger log, string? explicitAppPath = null)
    {
        _log = log;
        _explicitAppPath = explicitAppPath;
    }

    public bool TryLaunchApp()
    {
        var path = ResolveAppExe();
        if (path is null)
        {
            _log.Warning("Cannot resolve FocusAgent.App.exe path — no relaunch issued");
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory,
            };
            var p = Process.Start(startInfo);
            if (p is null)
            {
                _log.Warning("Process.Start returned null for {Path}", path);
                return false;
            }
            _log.Information("Launched FocusAgent.App from {Path} (pid {Pid})", path, p.Id);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to launch FocusAgent.App from {Path}", path);
            return false;
        }
    }

    private string? ResolveAppExe()
    {
        if (!string.IsNullOrWhiteSpace(_explicitAppPath))
        {
            return File.Exists(_explicitAppPath) ? _explicitAppPath : null;
        }

        // Packaged path: Package.Current.InstalledLocation works only when
        // the process was activated as part of an MSIX package. Outside of
        // that context the WinRT call throws InvalidOperationException
        // ("the requested operation requires application identity").
        try
        {
            var packagePath = Windows.ApplicationModel.Package.Current.InstalledLocation.Path;
            var packagedExe = Path.Combine(packagePath, "FocusAgent.App.exe");
            if (File.Exists(packagedExe)) return packagedExe;
        }
        catch
        {
            // Unpackaged — fall through to the sibling-exe heuristic.
        }

        // Sibling-exe (Watchdog and App in the same folder — dev / ad-hoc layout).
        var siblingExe = Path.Combine(AppContext.BaseDirectory, "FocusAgent.App.exe");
        if (File.Exists(siblingExe)) return siblingExe;

        // Parent-of-Watchdog layout: in the packaged MSIX the Watchdog lives
        // at <package>\Watchdog\, while the App is at <package>\. If
        // Package.Current is unavailable for some reason but the layout
        // matches, this fallback still finds the App.
        var parentDir = Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        if (!string.IsNullOrEmpty(parentDir))
        {
            var parentExe = Path.Combine(parentDir, "FocusAgent.App.exe");
            if (File.Exists(parentExe)) return parentExe;
        }

        return null;
    }
}
