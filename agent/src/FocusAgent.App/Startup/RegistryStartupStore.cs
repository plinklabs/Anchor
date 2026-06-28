using System.Runtime.Versioning;
using FocusAgent.Core.Startup;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace FocusAgent.App.Startup;

/// <summary>
/// The real <see cref="IStartupRegistrationStore"/>: reads/writes Anchor's
/// "start at login" value under <c>HKEY_CURRENT_USER\...\Run</c> via
/// <c>Microsoft.Win32.Registry</c> (#225). Per-user, so it needs no admin rights —
/// the whole reason Anchor uses HKCU rather than the managed-device HKLM path, and
/// the unpackaged-friendly equivalent of a packaged app's <c>windows.startupTask</c>.
///
/// The key root is injectable so the integration test can point the same code at a
/// throwaway HKCU subtree instead of the live Run key, and assert the real registry
/// write happened end-to-end without disturbing the dev's own auto-start entries.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryStartupStore : IStartupRegistrationStore
{
    private readonly string _keyPath;
    private readonly ILogger<RegistryStartupStore> _log;

    /// <param name="keyPathOverride">
    /// HKCU-relative key path to use instead of the production
    /// <see cref="StartupRunKey.RunKeyPath"/>. The integration test passes a
    /// throwaway subtree; production passes null.
    /// </param>
    public RegistryStartupStore(
        string? keyPathOverride = null,
        ILogger<RegistryStartupStore>? log = null)
    {
        _keyPath = keyPathOverride ?? StartupRunKey.RunKeyPath;
        _log = log ?? NullLogger<RegistryStartupStore>.Instance;
    }

    public string? GetRegisteredCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        return key?.GetValue(StartupRunKey.ValueName) as string;
    }

    public void SetRegisteredCommand(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
        key.SetValue(StartupRunKey.ValueName, command, RegistryValueKind.String);
    }

    public void RemoveRegistration()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
        if (key is null) return;
        if (key.GetValue(StartupRunKey.ValueName) is not null)
        {
            key.DeleteValue(StartupRunKey.ValueName, throwOnMissingValue: false);
            _log.LogInformation("Deleted run-at-login value {Name}.", StartupRunKey.ValueName);
        }
    }
}
