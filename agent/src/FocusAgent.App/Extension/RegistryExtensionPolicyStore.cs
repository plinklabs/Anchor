using System.Runtime.Versioning;
using FocusAgent.Core.Extension;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace FocusAgent.App.Extension;

/// <summary>
/// The real <see cref="IExtensionPolicyStore"/>: one Edge
/// <c>ExtensionInstallForcelist</c> key, read (and, where allowed, written) via
/// <c>Microsoft.Win32.Registry</c> (#211, #405).
///
/// The agent opens two of these: <see cref="ForMachine"/> (<c>HKLM</c>) and
/// <see cref="ForUser"/> (<c>HKCU</c>), and only <em>reads</em> them at runtime.
/// Neither is writable from the agent's unelevated process on a default Windows
/// profile: HKLM needs an administrator, and <c>HKCU\Software\Policies</c> gives
/// the user <c>ReadKey</c> only (just SYSTEM and an elevated Administrators token
/// may write it). The HKLM entry is therefore added by an elevated <c>reg.exe</c>
/// through one UAC prompt (<see cref="ShellElevatedPolicyWriter"/>); the write and
/// remove methods here serve the dev-only CLI mode, the uninstall clean-up of old
/// HKCU entries (where IT left the key writable), and the tests.
///
/// The hive and key are injectable so the integration tests can point the same
/// code at a throwaway HKCU subtree instead of the live Edge policy keys, and
/// assert the real registry access end-to-end without disturbing a dev's own Edge.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryExtensionPolicyStore : IExtensionPolicyStore
{
    private readonly RegistryHive _hive;
    private readonly string _keyPath;
    private readonly ILogger<RegistryExtensionPolicyStore> _log;

    /// <param name="hive">The hive the key lives in.</param>
    /// <param name="keyPath">The forcelist key path, relative to <paramref name="hive"/>.</param>
    /// <param name="log">Logger.</param>
    public RegistryExtensionPolicyStore(
        RegistryHive hive,
        string keyPath,
        ILogger<RegistryExtensionPolicyStore>? log = null)
    {
        _hive = hive;
        _keyPath = keyPath;
        _log = log ?? NullLogger<RegistryExtensionPolicyStore>.Instance;
    }

    /// <summary>
    /// The per-user forcelist under <c>HKCU</c>, or, with
    /// <paramref name="keyPathOverride"/>, an HKCU-relative throwaway key (tests).
    /// </summary>
    public static RegistryExtensionPolicyStore ForUser(
        string? keyPathOverride = null, ILogger<RegistryExtensionPolicyStore>? log = null) =>
        new(RegistryHive.CurrentUser, keyPathOverride ?? EdgeExtensionPolicy.ForcelistKeyPath, log);

    /// <summary>The machine-wide forcelist under <c>HKLM</c> (native 64-bit view, as Edge reads it).</summary>
    public static RegistryExtensionPolicyStore ForMachine(ILogger<RegistryExtensionPolicyStore>? log = null) =>
        new(RegistryHive.LocalMachine, EdgeExtensionPolicy.ForcelistKeyPath, log);

    public IReadOnlyList<string> GetForcelistEntries()
    {
        using var key = OpenKey(writable: false);
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
        using var key = OpenKey(writable: false);
        return key is null ? Array.Empty<string>() : key.GetValueNames();
    }

    public string? GetForcelistValue(string valueName)
    {
        using var key = OpenKey(writable: false);
        return key?.GetValue(valueName) as string;
    }

    public void AddForcelistEntry(string valueName, string entry)
    {
        using var baseKey = RegistryKey.OpenBaseKey(_hive, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(_keyPath, writable: true);
        key.SetValue(valueName, entry, RegistryValueKind.String);
    }

    public void RemoveAnchorForcelistEntries()
    {
        using var key = OpenKey(writable: true);
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

    private RegistryKey? OpenKey(bool writable)
    {
        // Registry64: the view Edge (a 64-bit process on 64-bit Windows) reads and
        // reg.exe /reg:64 writes. On 32-bit Windows it quietly maps to the only view.
        using var baseKey = RegistryKey.OpenBaseKey(_hive, RegistryView.Registry64);
        return baseKey.OpenSubKey(_keyPath, writable);
    }
}
