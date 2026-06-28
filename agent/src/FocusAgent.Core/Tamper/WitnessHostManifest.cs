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

    /// <summary>
    /// The optional production-auth config file the host reads (#289). MUST match
    /// <c>AuthConfig.FileName</c> in FocusAgent.WitnessHost (same cross-project
    /// duplication as <see cref="BackendUrlFileName"/>, locked by a test).
    /// </summary>
    public const string AuthConfigFileName = "auth-config.json";

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

    /// <summary>The auth-config.json path for a host exe living in <paramref name="hostDir"/>.</summary>
    public static string AuthConfigPathFor(string hostDir) =>
        Path.Combine(hostDir, AuthConfigFileName);

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

    /// <summary>
    /// The <c>auth-config.json</c> the host reads (#289):
    /// <c>{"tenantId":"…","clientId":"…","scope":"…"}</c> — the shape
    /// <c>AuthConfig</c> deserializes. Lets the agent hand the host its configured
    /// <c>Auth</c> section so the extension can mint a real student token in release.
    /// </summary>
    public static string BuildAuthConfigFile(WitnessAuthConfig auth)
    {
        ArgumentNullException.ThrowIfNull(auth);
        if (string.IsNullOrWhiteSpace(auth.TenantId) ||
            string.IsNullOrWhiteSpace(auth.ClientId) ||
            string.IsNullOrWhiteSpace(auth.Scope))
        {
            throw new ArgumentException(
                "TenantId, ClientId and Scope must all be provided.", nameof(auth));
        }

        return JsonSerializer.Serialize(
            new AuthConfigFile(auth.TenantId.Trim(), auth.ClientId.Trim(), auth.Scope.Trim()),
            Indented);
    }

    private sealed record Manifest(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("allowed_origins")] string[] AllowedOrigins);

    private sealed record BackendUrlFile(
        [property: JsonPropertyName("backendUrl")] string BackendUrl);

    private sealed record AuthConfigFile(
        [property: JsonPropertyName("tenantId")] string TenantId,
        [property: JsonPropertyName("clientId")] string ClientId,
        [property: JsonPropertyName("scope")] string Scope);
}

/// <summary>
/// The per-deployment Entra config the agent hands the witness host to write to
/// <c>auth-config.json</c> (#289). A plain carrier of the three values the
/// extension needs to acquire a student token; sourced from the agent's bound
/// <c>Auth</c> section. Lives beside <see cref="WitnessHostManifest"/> so the
/// registrar and the file builder share one shape without Core depending on the
/// host project or the App's settings types.
/// </summary>
public sealed record WitnessAuthConfig(string TenantId, string ClientId, string Scope);
