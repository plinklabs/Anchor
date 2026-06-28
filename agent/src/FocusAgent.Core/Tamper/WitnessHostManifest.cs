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
/// to a host-manifest JSON; that manifest names the absolute exe path and the pinned
/// extension ID in <c>allowed_origins</c>. The host in turn reads a
/// <c>backend-url.json</c> next to itself to learn which backend to hand the
/// extension (#204).
///
/// The pure shape lives here — separate from the Windows <c>Microsoft.Win32</c> /
/// filesystem writes — so the manifest/file content is unit-testable on any OS and
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

    /// <summary>
    /// The optional backend-URL config file the host reads. MUST match
    /// <c>BackendUrlConfig.FileName</c> in FocusAgent.WitnessHost (Core can't reference
    /// the host project, so the literal is duplicated here under test coverage).
    /// </summary>
    public const string BackendUrlFileName = "backend-url.json";

    private const string Description =
        "Anchor focus-agent witness link (#146): native-messaging bridge between the " +
        "Anchor extension and the on-box FocusAgent.";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The host manifest path for a host exe living in <paramref name="hostDir"/>.</summary>
    public static string ManifestPathFor(string hostDir) =>
        Path.Combine(hostDir, ManifestFileName);

    /// <summary>The backend-url.json path for a host exe living in <paramref name="hostDir"/>.</summary>
    public static string BackendUrlPathFor(string hostDir) =>
        Path.Combine(hostDir, BackendUrlFileName);

    /// <summary>
    /// The host-manifest JSON Edge consumes: the absolute exe path Edge launches plus
    /// the single pinned extension origin allowed to connect. Built with the JSON
    /// serializer so a Windows path's backslashes are escaped correctly (the dev
    /// script does the same by hand).
    /// </summary>
    public static string BuildManifest(string hostExePath)
    {
        if (string.IsNullOrWhiteSpace(hostExePath))
            throw new ArgumentException("Host exe path must be provided.", nameof(hostExePath));

        var manifest = new Manifest(
            Name: WitnessLink.NativeHostName,
            Description: Description,
            Path: hostExePath.Trim(),
            Type: "stdio",
            AllowedOrigins: new[] { $"chrome-extension://{EdgeExtensionPolicy.ExtensionId}/" });

        return JsonSerializer.Serialize(manifest, Indented);
    }

    /// <summary>
    /// The <c>backend-url.json</c> the host reads (<c>{"backendUrl":"…"}</c>) — the
    /// shape <c>BackendUrlConfig</c> deserializes. Lets the agent hand the host its
    /// configured <c>Backend:BaseUrl</c> so the extension targets the right backend in
    /// release instead of the dev fallback (#288 secondary gap).
    /// </summary>
    public static string BuildBackendUrlFile(string backendUrl)
    {
        if (string.IsNullOrWhiteSpace(backendUrl))
            throw new ArgumentException("Backend URL must be provided.", nameof(backendUrl));

        return JsonSerializer.Serialize(new BackendUrlFile(backendUrl.Trim()), Indented);
    }

    private sealed record Manifest(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("allowed_origins")] string[] AllowedOrigins);

    private sealed record BackendUrlFile(
        [property: JsonPropertyName("backendUrl")] string BackendUrl);
}
