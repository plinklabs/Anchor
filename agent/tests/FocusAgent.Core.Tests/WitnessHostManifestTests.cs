using System.Text.Json;
using FocusAgent.Core.Extension;
using FocusAgent.Core.Tamper;

namespace FocusAgent.Core.Tests;

/// <summary>
/// The on-disk/registry shape of the witness host registration (#288). These lock the
/// manifest content Edge actually consumes, and the constants the writer, remover, dev
/// script, and tests must share without drift.
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
    }

    [Fact]
    public void Manifest_sits_next_to_the_host_exe()
    {
        var dir = Path.GetDirectoryName(ExePath)!;

        Assert.Equal(Path.Combine(dir, "net.anchor.witness.json"), WitnessHostManifest.ManifestPathFor(dir));
    }

    [Fact]
    public void BuildManifest_emits_the_shape_edge_consumes()
    {
        var json = WitnessHostManifest.BuildManifest(ExePath, AnchorExtensionIds.StoreOnly);

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
    public void BuildManifest_lets_the_test_ring_build_connect_next_to_the_store_build()
    {
        // #497: Edge refuses connectNative to an origin the manifest doesn't name, so
        // without its origin here a test-ring laptop's extension could never check in.
        const string testRing = "abcdefghijklmnopabcdefghijklmnop";

        var json = WitnessHostManifest.BuildManifest(ExePath, AnchorExtensionIds.WithTestRing(testRing));

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(
            [$"chrome-extension://{EdgeExtensionPolicy.ExtensionId}/", $"chrome-extension://{testRing}/"],
            doc.RootElement.GetProperty("allowed_origins").EnumerateArray().Select(o => o.GetString()));
    }

    [Fact]
    public void BuildManifest_defaults_to_the_ids_this_agent_was_built_with()
    {
        using var doc = JsonDocument.Parse(WitnessHostManifest.BuildManifest(ExePath));

        Assert.Equal(
            AnchorExtensionIds.Configured.All.Select(id => $"chrome-extension://{id}/"),
            doc.RootElement.GetProperty("allowed_origins").EnumerateArray().Select(o => o.GetString()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildManifest_rejects_a_blank_exe_path(string exePath)
    {
        Assert.Throws<ArgumentException>(() => WitnessHostManifest.BuildManifest(exePath));
    }
}
