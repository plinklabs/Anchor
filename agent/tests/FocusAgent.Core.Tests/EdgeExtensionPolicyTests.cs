using FocusAgent.Core.Extension;

namespace FocusAgent.Core.Tests;

public class EdgeExtensionPolicyTests
{
    [Fact]
    public void ForcelistEntry_is_id_semicolon_updateurl()
    {
        Assert.Equal(
            $"{EdgeExtensionPolicy.ExtensionId};{EdgeExtensionPolicy.UpdateUrl}",
            EdgeExtensionPolicy.ForcelistEntry);
    }

    [Fact]
    public void ExtensionId_matches_the_pinned_stable_id()
    {
        // Locks the ID to extension/README.md's pinned stable ID. A drift here
        // would write a policy that pins the wrong (or no) extension.
        Assert.Equal("akkfdaclmpfcnjalcifkcbhgjnnopman", EdgeExtensionPolicy.ExtensionId);
    }

    [Fact]
    public void Forcelist_key_path_is_the_per_user_HKCU_edge_policy()
    {
        Assert.Equal(
            @"Software\Policies\Microsoft\Edge\ExtensionInstallForcelist",
            EdgeExtensionPolicy.ForcelistKeyPath);
    }

    [Theory]
    [InlineData("akkfdaclmpfcnjalcifkcbhgjnnopman;https://edge.microsoft.com/extensionwebstorebase/v1/crx", true)]
    [InlineData("akkfdaclmpfcnjalcifkcbhgjnnopman", true)]                    // id only, no update-url
    [InlineData("AKKFDACLMPFCNJALCIFKCBHGJNNOPMAN;https://x", true)]          // case-insensitive
    [InlineData(" akkfdaclmpfcnjalcifkcbhgjnnopman ;https://x", true)]        // padded
    [InlineData("someotherextensionidaaaaaaaaaaaa;https://x", false)]         // different extension
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAnchorEntry_matches_only_our_id_segment(string? value, bool expected)
    {
        Assert.Equal(expected, EdgeExtensionPolicy.IsAnchorEntry(value));
    }

    [Fact]
    public void NextFreeIndex_is_1_when_empty()
    {
        Assert.Equal("1", EdgeExtensionPolicy.NextFreeIndex(Array.Empty<string>()));
    }

    [Fact]
    public void NextFreeIndex_skips_taken_slots_without_clobbering_a_coinstalled_entry()
    {
        // A different product already holds slots 1 and 2; we must take 3.
        Assert.Equal("3", EdgeExtensionPolicy.NextFreeIndex(new[] { "1", "2" }));
    }

    [Fact]
    public void NextFreeIndex_fills_the_lowest_gap()
    {
        Assert.Equal("2", EdgeExtensionPolicy.NextFreeIndex(new[] { "1", "3" }));
    }

    [Fact]
    public void NextFreeIndex_ignores_non_numeric_value_names()
    {
        Assert.Equal("1", EdgeExtensionPolicy.NextFreeIndex(new[] { "notanumber", "" }));
    }

    [Fact]
    public void StoreListingUrl_targets_the_pinned_id_on_the_edge_addons_store()
    {
        Assert.Contains("microsoftedge.microsoft.com/addons", EdgeExtensionPolicy.StoreListingUrl);
        Assert.EndsWith(EdgeExtensionPolicy.ExtensionId, EdgeExtensionPolicy.StoreListingUrl);
    }
}
