using System.Runtime.Versioning;
using FocusAgent.Core.Extension;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace FocusAgent.App.Extension;

/// <summary>
/// The real <see cref="IExtensionPolicyStore"/>: reads/writes the Edge
/// <c>ExtensionInstallForcelist</c> policy under <c>HKEY_CURRENT_USER</c> via
/// <c>Microsoft.Win32.Registry</c> (#211). Per-user, so it needs no admin rights —
/// the whole reason Anchor uses HKCU rather than the managed-device HKLM path.
///
/// The key root is injectable so the integration test can point the same code at
/// a throwaway HKCU subtree instead of the live Edge policy key, and assert the
/// real registry write happened end-to-end without disturbing a dev's own Edge.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryExtensionPolicyStore : IExtensionPolicyStore
{
    private readonly string _keyPath;
    private readonly ILogger<RegistryExtensionPolicyStore> _log;

    /// <param name="keyPathOverride">
    /// HKCU-relative key path to use instead of the production
    /// <see cref="EdgeExtensionPolicy.ForcelistKeyPath"/>. The integration test
    /// passes a throwaway subtree; production passes null.
    /// </param>
    public RegistryExtensionPolicyStore(
        string? keyPathOverride = null,
        ILogger<RegistryExtensionPolicyStore>? log = null)
    {
        _keyPath = keyPathOverride ?? EdgeExtensionPolicy.ForcelistKeyPath;
        _log = log ?? NullLogger<RegistryExtensionPolicyStore>.Instance;
    }

    public IReadOnlyList<string> GetForcelistEntries()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        if (key is null) return Array.Empty<string>();

        var entries = new List<string>();
        foreach (var name in key.GetValueNames())
        {
            if (key.GetValue(name) is string s)
                entries.Add(s);
        }
        return entries;
    }

    public IReadOnlyList<string> GetForcelistValueNames()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        return key is null ? Array.Empty<string>() : key.GetValueNames();
    }

    public void AddForcelistEntry(string valueName, string entry)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
        key.SetValue(valueName, entry, RegistryValueKind.String);
    }

    public void RemoveAnchorForcelistEntries()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
        if (key is null) return;

        foreach (var name in key.GetValueNames())
        {
            if (key.GetValue(name) is string s && EdgeExtensionPolicy.IsAnchorEntry(s))
            {
                key.DeleteValue(name, throwOnMissingValue: false);
                _log.LogInformation("Deleted forcelist value {Name} ({Entry}).", name, s);
            }
        }
    }
}
