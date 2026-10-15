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
        Assert.Equal("dnkimhodjfogjibnbbfdjdapgmmiojio", EdgeExtensionPolicy.ExtensionId);
    }

    [Fact]
    public void Forcelist_key_path_is_the_edge_policy_path_under_either_hive()
    {
        Assert.Equal(
            @"Software\Policies\Microsoft\Edge\ExtensionInstallForcelist",
            EdgeExtensionPolicy.ForcelistKeyPath);
        // #405: the agent adds its entry machine-wide.
        Assert.Equal(
            @"HKLM\Software\Policies\Microsoft\Edge\ExtensionInstallForcelist",
            EdgeExtensionPolicy.MachineForcelistRegPath);
    }

    [Fact]
    public void RegAddArguments_adds_only_our_value_at_the_given_slot_in_the_64bit_view()
    {
        // #405: the exact command line the UAC-elevated reg.exe runs. One value, at
        // one slot, forced (no interactive "overwrite?" in a hidden window).
        Assert.Equal(
            @"add ""HKLM\Software\Policies\Microsoft\Edge\ExtensionInstallForcelist"" /v 3 /t REG_SZ " +
            @"/d ""dnkimhodjfogjibnbbfdjdapgmmiojio;https://edge.microsoft.com/extensionwebstorebase/v1/crx"" /f /reg:64",
            EdgeExtensionPolicy.RegAddArguments("3", EdgeExtensionPolicy.ForcelistEntry));
    }

    [Fact]
    public void RegDeleteArguments_deletes_one_value_never_the_key()
    {
        Assert.Equal(
            @"delete ""HKLM\Software\Policies\Microsoft\Edge\ExtensionInstallForcelist"" /v 2 /f /reg:64",
            EdgeExtensionPolicy.RegDeleteArguments("2"));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("12", true)]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("1 /va", false)]   // can't smuggle "delete every value" into reg.exe
    [InlineData("1\" /f", false)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSlotName_accepts_only_positive_numbers(string? name, bool expected)
    {
        Assert.Equal(expected, EdgeExtensionPolicy.IsSlotName(name));
    }

    [Theory]
    [InlineData("1 /va")]
    [InlineData("x")]
    public void Reg_arguments_refuse_a_value_name_that_is_not_a_slot(string name)
    {
        Assert.Throws<ArgumentException>(() => EdgeExtensionPolicy.RegDeleteArguments(name));
        Assert.Throws<ArgumentException>(() => EdgeExtensionPolicy.RegAddArguments(name, EdgeExtensionPolicy.ForcelistEntry));
    }

    [Theory]
    [InlineData("dnkimhodjfogjibnbbfdjdapgmmiojio;https://edge.microsoft.com/extensionwebstorebase/v1/crx", true)]
    [InlineData("dnkimhodjfogjibnbbfdjdapgmmiojio", true)]                    // id only, no update-url
    [InlineData("DNKIMHODJFOGJIBNBBFDJDAPGMMIOJIO;https://x", true)]          // case-insensitive
    [InlineData(" dnkimhodjfogjibnbbfdjdapgmmiojio ;https://x", true)]        // padded
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
