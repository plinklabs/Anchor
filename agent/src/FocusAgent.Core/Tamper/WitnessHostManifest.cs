using System.Text.Json;
using System.Text.Json.Serialization;
using FocusAgent.Core.Extension;

namespace FocusAgent.Core.Tamper;

/// <summary>
/// The identity + on-disk/registry shape of the witness native-messaging host
/// registration (#288) — the agent-side equivalent of the dev-only
/// <c>scripts/dev/register-witness-host.ps1</c>, so a release install registers the
/// host the same way a dev box does.
///
/// Edge launches <c>anchor-witness-host.exe</c> when the extension calls
/// <c>chrome.runtime.connectNative(<see cref="WitnessLink.NativeHostName"/>)</c>. It
/// finds the host through a per-user registry key
/// (<see cref="RegistryKeyPath"/>) whose <c>(default)</c> value is the absolute path
/// to a host-manifest JSON; that manifest names the absolute exe path and Anchor's
/// extension IDs in <c>allowed_origins</c> (<see cref="AnchorExtensionIds"/>).
///
/// The pure shape lives here — separate from the Windows <c>Microsoft.Win32</c> /
/// filesystem writes — so the manifest content is unit-testable on any OS and
/// the constants can't drift between the writer, the remover, the dev script, and
/// the tests. Per-user (HKCU), so it needs no admin — the same unmanaged-BYOD reason
/// <see cref="EdgeExtensionPolicy"/> and <c>StartupRunKey</c> use HKCU.
/// </summary>
public static class WitnessHostManifest
{
    /// <summary>
    /// The per-user registry key Edge reads to locate the host. Its <c>(default)</c>
    /// value is the absolute path to the host manifest. Keyed by the reverse-DNS host
    /// name so the key is exclusively Anchor's (no co-tenant value to preserve, unlike
    /// the forcelist / Run cases).
    /// </summary>
    public const string RegistryKeyPath =
        @"Software\Microsoft\Edge\NativeMessagingHosts\" + WitnessLink.NativeHostName;

    /// <summary>
    /// The host exe Edge launches. Matches the <c>FocusAgent.WitnessHost</c> project's
    /// <c>AssemblyName</c>; the release publish (pack-release.ps1) lands it next to the
    /// agent, which is where the agent looks for it at startup.
    /// </summary>
    public const string HostExeName = "anchor-witness-host.exe";

    /// <summary>
    /// The host-manifest JSON file name. Written next to the host exe (where a
    /// <c>git clean</c> of the repo can't clobber it); its absolute path is what the
    /// registry key points at. Named after the host so it's recognisably ours.
    /// </summary>
    public const string ManifestFileName = WitnessLink.NativeHostName + ".json";

    private const string Description =
        "Anchor focus-agent witness link (#146): native-messaging bridge between the " +
        "Anchor extension and the on-box FocusAgent.";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The host manifest path for a host exe living in <paramref name="hostDir"/>.</summary>
    public static string ManifestPathFor(string hostDir) =>
        Path.Combine(hostDir, ManifestFileName);

    /// <summary>
    /// The host-manifest JSON Edge consumes: the absolute exe path Edge launches plus
    /// the extension origins allowed to connect, one per ID in
    /// <paramref name="extensionIds"/> (default
    /// <see cref="AnchorExtensionIds.Configured"/>): the store build's, and the
    /// self-hosted test-ring build's once one is configured (#497). Edge refuses
    /// <c>connectNative</c> to any other extension. Built with the JSON serializer
    /// so a Windows path's backslashes are escaped correctly (the dev script does
    /// the same by hand).
    /// </summary>
    public static string BuildManifest(string hostExePath, AnchorExtensionIds? extensionIds = null)
    {
        if (string.IsNullOrWhiteSpace(hostExePath))
            throw new ArgumentException("Host exe path must be provided.", nameof(hostExePath));

        var manifest = new Manifest(
            Name: WitnessLink.NativeHostName,
            Description: Description,
            Path: hostExePath.Trim(),
            Type: "stdio",
            AllowedOrigins: (extensionIds ?? AnchorExtensionIds.Configured).All
                .Select(id => $"chrome-extension://{id}/")
                .ToArray());

        return JsonSerializer.Serialize(manifest, Indented);
    }

    private sealed record Manifest(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("allowed_origins")] string[] AllowedOrigins);
}
