using System.Text.Json;
using FocusAgent.Core.Extension;
using FocusAgent.Core.Tamper;

namespace FocusAgent.Core.Tests;

/// <summary>
/// The on-disk/registry shape of the witness host registration (#288). These lock the
/// manifest + backend-url content Edge / the host actually consume, and the constants
/// the writer, remover, dev script, and tests must share without drift.
/// </summary>
public class WitnessHostManifestTests
{
    private const string ExePath = @"C:\Users\bob\AppData\Local\Anchor.Agent\current\anchor-witness-host.exe";

    [Fact]
    public void RegistryKey_is_the_per_user_edge_native_messaging_host_path()
    {
        Assert.Equal(
            @"Software\Microsoft\Edge\NativeMessagingHosts\net.anchor.witness",
            WitnessHostManifest.RegistryKeyPath);
        // The leaf must equal the reverse-DNS host name the extension connectNative()s.
        Assert.EndsWith(WitnessLink.NativeHostName, WitnessHostManifest.RegistryKeyPath);
    }

    [Fact]
    public void Constants_match_the_host_exe_and_file_names()
    {
        // The exe name Edge launches must match FocusAgent.WitnessHost's AssemblyName.
        Assert.Equal("anchor-witness-host.exe", WitnessHostManifest.HostExeName);
        Assert.Equal("net.anchor.witness.json", WitnessHostManifest.ManifestFileName);
        // Must equal BackendUrlConfig.FileName in the host project (Core can't reference it).
        Assert.Equal("backend-url.json", WitnessHostManifest.BackendUrlFileName);
        // Must equal AuthConfig.FileName in the host project, likewise (#289).
        Assert.Equal("auth-config.json", WitnessHostManifest.AuthConfigFileName);
    }

    [Fact]
    public void Manifest_and_config_files_sit_next_to_the_host_exe()
    {
        var dir = Path.GetDirectoryName(ExePath)!;

        Assert.Equal(Path.Combine(dir, "net.anchor.witness.json"), WitnessHostManifest.ManifestPathFor(dir));
        Assert.Equal(Path.Combine(dir, "backend-url.json"), WitnessHostManifest.BackendUrlPathFor(dir));
        Assert.Equal(Path.Combine(dir, "auth-config.json"), WitnessHostManifest.AuthConfigPathFor(dir));
    }

    [Fact]
    public void BuildManifest_emits_the_shape_edge_consumes()
    {
        var json = WitnessHostManifest.BuildManifest(ExePath);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("net.anchor.witness", root.GetProperty("name").GetString());
        Assert.Equal("stdio", root.GetProperty("type").GetString());
        // The absolute exe path round-trips through JSON (backslashes escaped correctly).
        Assert.Equal(ExePath, root.GetProperty("path").GetString());

        var origins = root.GetProperty("allowed_origins");
        var origin = Assert.Single(origins.EnumerateArray());
        // Only the pinned extension may connect.
        Assert.Equal($"chrome-extension://{EdgeExtensionPolicy.ExtensionId}/", origin.GetString());
    }

    [Fact]
    public void BuildBackendUrlFile_emits_the_shape_backendurlconfig_parses()
    {
        var json = WitnessHostManifest.BuildBackendUrlFile("https://anchor.example/");

        using var doc = JsonDocument.Parse(json);
        // BackendUrlConfig deserializes {"backendUrl":"…"}; it normalises the slash on read.
        Assert.Equal("https://anchor.example/", doc.RootElement.GetProperty("backendUrl").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildManifest_rejects_a_blank_exe_path(string exePath)
    {
        Assert.Throws<ArgumentException>(() => WitnessHostManifest.BuildManifest(exePath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildBackendUrlFile_rejects_a_blank_url(string url)
    {
        Assert.Throws<ArgumentException>(() => WitnessHostManifest.BuildBackendUrlFile(url));
    }

    [Fact]
    public void BuildAuthConfigFile_emits_the_shape_authconfig_parses()
    {
        var json = WitnessHostManifest.BuildAuthConfigFile(
            new WitnessAuthConfig(" tenant-1 ", " client-1 ", " api://x/.default "));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        // AuthConfig deserializes {"tenantId":"…","clientId":"…","scope":"…"} and the
        // values are trimmed on write so a stray space in config can't break sign-in.
        Assert.Equal("tenant-1", root.GetProperty("tenantId").GetString());
        Assert.Equal("client-1", root.GetProperty("clientId").GetString());
        Assert.Equal("api://x/.default", root.GetProperty("scope").GetString());
    }

    [Theory]
    [InlineData("", "client", "scope")]
    [InlineData("tenant", "   ", "scope")]
    [InlineData("tenant", "client", "")]
    public void BuildAuthConfigFile_rejects_a_partial_config(string tenantId, string clientId, string scope)
    {
        Assert.Throws<ArgumentException>(
            () => WitnessHostManifest.BuildAuthConfigFile(new WitnessAuthConfig(tenantId, clientId, scope)));
    }
}
