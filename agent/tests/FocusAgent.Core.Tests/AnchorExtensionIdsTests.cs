using System.Text.Json;
using FocusAgent.Core.Extension;

namespace FocusAgent.Core.Tests;

/// <summary>
/// The extension IDs the agent takes for Anchor's (#497): always the store build's,
/// plus the self-hosted test-ring build's once <c>extension/test-ring.json</c> names
/// one. Without one the agent takes exactly what it took before.
/// </summary>
public class AnchorExtensionIdsTests
{
    private const string Store = EdgeExtensionPolicy.ExtensionId;
    private const string TestRing = "abcdefghijklmnopabcdefghijklmnop";
    private const string TestRingUpdateUrl = "https://github.com/plinklabs/Anchor/releases/download/extension-test-ring/updates.xml";

    [Fact]
    public void Store_only_is_the_store_build_alone()
    {
        Assert.Null(AnchorExtensionIds.StoreOnly.TestRing);
        Assert.Equal([Store], AnchorExtensionIds.StoreOnly.All);
    }

    [Fact]
    public void A_test_ring_id_comes_after_the_store_one()
    {
        var ids = AnchorExtensionIds.WithTestRing(TestRing);

        Assert.Equal(TestRing, ids.TestRing);
        Assert.Equal([Store, TestRing], ids.All);
    }

    [Fact]
    public void A_test_ring_id_is_trimmed_and_lower_cased_as_Edge_writes_it()
    {
        Assert.Equal(TestRing, AnchorExtensionIds.WithTestRing($"  {TestRing.ToUpperInvariant()} ").TestRing);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abcdefghijklmnopabcdefghijklmno")]   // 31 letters
    [InlineData("abcdefghijklmnopabcdefghijklmnopa")] // 33
    [InlineData("abcdefghijklmnopabcdefghijklmnoq")]  // q is past p
    [InlineData("0123456789abcdef0123456789abcdef")]  // hex, not Edge's letters
    public void Anything_but_an_extension_id_means_no_test_ring(string? id)
    {
        Assert.Same(AnchorExtensionIds.StoreOnly, AnchorExtensionIds.WithTestRing(id));
    }

    [Fact]
    public void The_store_id_as_test_ring_is_not_listed_twice()
    {
        Assert.Equal([Store], AnchorExtensionIds.WithTestRing(Store).All);
    }

    [Theory]
    [InlineData(Store + ";" + EdgeExtensionPolicy.UpdateUrl)]
    [InlineData(TestRing + ";" + TestRingUpdateUrl)] // what Intune's test-ring profile writes
    [InlineData(TestRing)]                           // id only
    [InlineData(" ABCDEFGHIJKLMNOPABCDEFGHIJKLMNOP ;https://x")]
    public void With_a_test_ring_both_builds_entries_are_Anchors(string entry)
    {
        Assert.True(AnchorExtensionIds.WithTestRing(TestRing).MatchesForcelistEntry(entry));
    }

    [Fact]
    public void Without_a_test_ring_only_the_store_entry_is_Anchors_as_before()
    {
        Assert.True(AnchorExtensionIds.StoreOnly.MatchesForcelistEntry(EdgeExtensionPolicy.ForcelistEntry));
        Assert.False(AnchorExtensionIds.StoreOnly.MatchesForcelistEntry($"{TestRing};{TestRingUpdateUrl}"));
    }

    [Theory]
    [InlineData("someotherextensionidaaaaaaaaaaaa;https://x")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_entries_are_never_Anchors(string? entry)
    {
        Assert.False(AnchorExtensionIds.WithTestRing(TestRing).MatchesForcelistEntry(entry));
    }

    [Theory]
    [InlineData("""{ "extensionId": "abcdefghijklmnopabcdefghijklmnop", "publicKey": "MIIB" }""", TestRing)]
    [InlineData("""{ "extensionId": "", "publicKey": "" }""", null)] // the committed state until the key exists
    [InlineData("""{ "publicKey": "MIIB" }""", null)]
    [InlineData("""{ "extensionId": 42 }""", null)]
    [InlineData("""{ "extensionId": "not-an-id" }""", null)]
    [InlineData("""[ "abcdefghijklmnopabcdefghijklmnop" ]""", null)]
    [InlineData("""{ "extensionId": """, null)]
    public void Reads_the_test_ring_id_from_the_config_file(string json, string? expected)
    {
        Assert.Equal(expected, AnchorExtensionIds.ReadTestRingId(json));
    }

    [Fact]
    public void The_build_takes_its_test_ring_id_from_extension_test_ring_json()
    {
        // The one place the ID is configured; the csproj embeds it. A wrong path
        // there would quietly leave every agent build without the test ring.
        var json = File.ReadAllText(TestRingConfigPath());
        var expected = AnchorExtensionIds.ReadTestRingId(json);

        Assert.Equal(expected, AnchorExtensionIds.Configured.TestRing);
        using var embedded = typeof(AnchorExtensionIds).Assembly
            .GetManifestResourceStream(AnchorExtensionIds.TestRingResourceName);
        Assert.NotNull(embedded);
        Assert.Equal(json, new StreamReader(embedded).ReadToEnd());
    }

    [Fact]
    public void The_committed_test_ring_id_is_empty_or_an_extension_id()
    {
        // A malformed ID reads as none at run time (the agent never takes less than
        // the store build), so a typo has to fail here instead of shipping quietly.
        using var doc = JsonDocument.Parse(File.ReadAllText(TestRingConfigPath()));
        var id = doc.RootElement.GetProperty("extensionId").GetString();

        Assert.True(id == "" || AnchorExtensionIds.IsExtensionId(id), $"'{id}' is not an extension ID.");
    }

    private static string TestRingConfigPath()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "extension", "test-ring.json");
            if (File.Exists(candidate) && Directory.Exists(Path.Combine(dir, "agent")))
                return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("Could not locate extension/test-ring.json above the test output.");
    }
}
