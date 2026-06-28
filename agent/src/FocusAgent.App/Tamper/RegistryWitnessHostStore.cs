using System.Runtime.Versioning;
using FocusAgent.Core.Tamper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace FocusAgent.App.Tamper;

/// <summary>
/// The real <see cref="IWitnessHostStore"/>: reads/writes the witness host's HKCU
/// <c>NativeMessagingHosts</c> key via <c>Microsoft.Win32.Registry</c>, and the
/// manifest / backend-url files via the filesystem (#288). Per-user (HKCU), so it
/// needs no admin — the same unmanaged-BYOD reason the force-install and Run-key
/// stores use HKCU.
///
/// The key root is injectable so the integration test can point the same code at a
/// throwaway HKCU subtree instead of the live Edge native-messaging key, and assert
/// the real registry write happened end-to-end without disturbing a dev's own host
/// registration.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryWitnessHostStore : IWitnessHostStore
{
    private readonly string _keyPath;
    private readonly ILogger<RegistryWitnessHostStore> _log;

    /// <param name="keyPathOverride">
    /// HKCU-relative key path to use instead of the production
    /// <see cref="WitnessHostManifest.RegistryKeyPath"/>. The integration test passes a
    /// throwaway subtree; production passes null.
    /// </param>
    public RegistryWitnessHostStore(
        string? keyPathOverride = null,
        ILogger<RegistryWitnessHostStore>? log = null)
    {
        _keyPath = keyPathOverride ?? WitnessHostManifest.RegistryKeyPath;
        _log = log ?? NullLogger<RegistryWitnessHostStore>.Instance;
    }

    public string? GetRegisteredManifestPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        // Edge reads the manifest path from the key's (default) value.
        return key?.GetValue(null) as string;
    }

    public void SetRegisteredManifestPath(string manifestPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
        key.SetValue(null, manifestPath, RegistryValueKind.String);
    }

    public void RemoveRegistration()
    {
        // The key is exclusively ours (named after net.anchor.witness), so deleting the
        // whole key — not a single value — is correct, and leaves any other product's
        // native-messaging hosts (sibling keys) untouched.
        using (var key = Registry.CurrentUser.OpenSubKey(_keyPath))
        {
            if (key is null) return;
        }

        Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);
        _log.LogInformation("Deleted witness host key {Key}.", _keyPath);
    }

    public string? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            // An unreadable file just means "stale" — fall through to a rewrite.
            return null;
        }
    }

    public void WriteFile(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, content);
    }
}
