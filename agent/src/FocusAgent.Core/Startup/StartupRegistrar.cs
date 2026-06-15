using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusAgent.Core.Startup;

/// <summary>
/// Manages the agent's per-user "start at login" registration under
/// <c>HKCU\...\Run</c> (#225), replacing the MSIX-only <c>windows.startupTask</c>
/// extension for the unpackaged Velopack build.
///
/// On install / first-run (and again on every update, since a Velopack update
/// lands in a new versioned install dir) the agent ensures the Run value points at
/// the <em>current</em> installed exe; on uninstall it removes it, leaving the box
/// as it found it. Both are idempotent: re-running never leaks a duplicate or stale
/// entry — <see cref="EnsureRegistered"/> only writes when the value is missing or
/// points somewhere else.
///
/// Pure orchestration — the registry write (<see cref="IStartupRegistrationStore"/>)
/// is injected — so the present/update/remove logic is unit-testable on any OS
/// without a real registry, while the real HKCU path is exercised end-to-end by the
/// integration test.
/// </summary>
public sealed class StartupRegistrar
{
    private readonly IStartupRegistrationStore _store;
    private readonly ILogger<StartupRegistrar> _log;

    public StartupRegistrar(
        IStartupRegistrationStore store,
        ILogger<StartupRegistrar>? log = null)
    {
        _store = store;
        _log = log ?? NullLogger<StartupRegistrar>.Instance;
    }

    /// <summary>
    /// Ensure the Run value is present and points at <paramref name="exePath"/>,
    /// returning whether a write happened. Idempotent: if Anchor's value is already
    /// the command for this exe it's a no-op; if it's missing or points at a stale
    /// path (a previous version's install dir) it's (re-)written. This is what makes
    /// it safe to call on first run <em>and</em> on every update.
    /// </summary>
    public bool EnsureRegistered(string exePath)
    {
        var desired = StartupRunKey.CommandFor(exePath);
        var current = _store.GetRegisteredCommand();

        if (StartupRunKey.CommandTargets(current, exePath))
        {
            _log.LogInformation(
                "Run-at-login entry already points at {ExePath}; not re-writing.", exePath);
            return false;
        }

        _store.SetRegisteredCommand(desired);
        _log.LogInformation(
            current is null
                ? "Wrote run-at-login entry: HKCU\\{Key}\\{Name} = {Command}"
                : "Updated run-at-login entry: HKCU\\{Key}\\{Name} = {Command}",
            StartupRunKey.RunKeyPath, StartupRunKey.ValueName, desired);
        return true;
    }

    /// <summary>
    /// Remove Anchor's Run value — called from the uninstall hook so uninstalling
    /// the agent stops it launching at login. Best-effort and idempotent: a missing
    /// value is fine, and any failure is logged rather than thrown so it can never
    /// block the uninstall.
    /// </summary>
    public void Remove()
    {
        try
        {
            _store.RemoveRegistration();
            _log.LogInformation("Removed run-at-login entry {Name}.", StartupRunKey.ValueName);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Removing the run-at-login entry failed (uninstall).");
        }
    }
}
