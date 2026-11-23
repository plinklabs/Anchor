using System.Text.Json;
using System.Text.Json.Nodes;
using FocusAgent.Core.Tamper;

namespace FocusAgent.Core.Tests;

/// <summary>
/// #518: telling "this profile was closed" from "the extension was switched off
/// in a profile that is still open", from what Edge keeps outside the witness
/// link: its windows' AppUserModelIDs and each profile's preferences.
/// </summary>
public sealed class EdgeOpenProfileExtensionProbeTests : IDisposable
{
    private const string TestRingId = "fgnfeccjliecdbflngplpfhakgpjcckh";

    private readonly FakeEdgeProfiles _profiles = new();
    private readonly FakeAppIds _windows = new();

    public void Dispose() => _profiles.Dispose();

    private EdgeOpenProfileExtensionProbe Probe(params string[] ids) =>
        new(_profiles.UserData, _windows, "msedge", ids.Length > 0 ? ids : new[] { FakeEdgeProfiles.ExtensionId });

    [Fact]
    public void An_open_profile_with_the_extension_on_is_not_found()
    {
        _profiles.SwitchOn("Default");
        _profiles.SwitchOn("Profile 1");
        _windows.Ids.AddRange(["MSEdge", "MSEdge.UserData.Profile1"]);

        Assert.Empty(Probe().FindSwitchedOff());
    }

    [Fact]
    public void An_open_profile_with_the_extension_switched_off_is_found()
    {
        _profiles.SwitchOn("Default");
        _profiles.SwitchOff("Profile 1");
        _windows.Ids.AddRange(["MSEdge", "MSEdge.UserData.Profile1"]);

        Assert.Equal(new[] { "Profile 1" }, Probe().FindSwitchedOff());
    }

    [Fact]
    public void A_closed_profile_with_the_extension_switched_off_is_not_found()
    {
        _profiles.SwitchOn("Default");
        _profiles.SwitchOff("Profile 1");
        _windows.Ids.Add("MSEdge");

        Assert.Empty(Probe().FindSwitchedOff());
    }

    [Fact]
    public void The_default_profile_s_windows_are_plain_MSEdge()
    {
        _profiles.SwitchOff("Default");
        var probe = Probe();

        _windows.Ids.Add("MSEdge.UserData.Profile1");
        Assert.Empty(probe.FindSwitchedOff());

        _windows.Ids.Add("MSEdge");
        Assert.Equal(new[] { "Default" }, probe.FindSwitchedOff());
    }

    [Fact]
    public void An_app_window_of_the_profile_does_not_count_as_the_profile_being_open()
    {
        // An installed site runs in a window whose ID starts with the app's.
        _profiles.SwitchOff("Profile 1");
        _windows.Ids.Add("MSEdge._crx_abcdefgh.UserData.Profile1");
        _windows.Ids.Add("_crx_abcdefgh.UserData.Profile1");

        Assert.Empty(Probe().FindSwitchedOff());
    }

    [Fact]
    public void A_profile_that_never_had_the_extension_is_not_found()
    {
        _profiles.SwitchOn("Default");
        _profiles.Remove("Profile 2");
        _windows.Ids.AddRange(["MSEdge", "MSEdge.UserData.Profile2"]);

        Assert.Empty(Probe().FindSwitchedOff());
    }

    [Fact]
    public void The_profiles_with_the_extension_installed_are_found_on_or_off_and_open_or_not()
    {
        // #556: where the extension is installed, for a link that never came up.
        _profiles.SwitchOn("Default");
        _profiles.SwitchOff("Profile 1");
        _profiles.SwitchOn("Profile 2");
        _profiles.Remove("Profile 3");
        _windows.Ids.AddRange(["MSEdge", "MSEdge.UserData.Profile1", "MSEdge.UserData.Profile3"]);
        var probe = Probe();

        Assert.Equal(
            new[]
            {
                new ProfileWithExtension("Default", On: true),
                new ProfileWithExtension("Profile 1", On: false),
                new ProfileWithExtension("Profile 2", On: true),
            },
            probe.FindWithExtension(openOnly: false).OrderBy(p => p.Profile));
        // Profile 2 is closed, and Profile 3, though open, never had it.
        Assert.Equal(
            new[] { new ProfileWithExtension("Default", On: true), new ProfileWithExtension("Profile 1", On: false) },
            probe.FindWithExtension(openOnly: true).OrderBy(p => p.Profile));
    }

    [Fact]
    public void No_profile_with_the_extension_is_found_where_none_has_it()
    {
        _profiles.Remove("Default");
        _windows.Ids.Add("MSEdge");

        Assert.Empty(Probe().FindWithExtension(openOnly: false));
        Assert.Empty(Probe().FindWithExtension(openOnly: true));
    }

    [Fact]
    public void An_extension_removed_from_an_open_profile_is_found_once_it_was_seen_there()
    {
        _profiles.SwitchOn("Profile 1");
        _windows.Ids.Add("MSEdge.UserData.Profile1");
        var probe = Probe();
        Assert.Empty(probe.FindSwitchedOff());

        _profiles.Remove("Profile 1");
        Assert.Equal(new[] { "Profile 1" }, probe.FindSwitchedOff());

        // Installed again: covered again.
        _profiles.SwitchOn("Profile 1");
        Assert.Empty(probe.FindSwitchedOff());
    }

    [Fact]
    public void Switching_it_back_on_is_seen_on_the_next_read()
    {
        _profiles.SwitchOff("Profile 1");
        _windows.Ids.Add("MSEdge.UserData.Profile1");
        var probe = Probe();
        Assert.Single(probe.FindSwitchedOff());

        _profiles.SwitchOn("Profile 1");

        Assert.Empty(probe.FindSwitchedOff());
    }

    [Fact]
    public void One_of_anchor_s_builds_on_is_enough()
    {
        // A test-ring laptop: the store build switched off, the test-ring build on.
        _profiles.Write("Profile 1", FakeEdgeProfiles.ExtensionId, null);
        var path = Path.Combine(_profiles.ProfileFolder("Profile 1"), "Secure Preferences");
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        var settings = root["extensions"]!["settings"]!.AsObject();
        settings[FakeEdgeProfiles.ExtensionId] = new JsonObject { ["location"] = 1, ["disable_reasons"] = new JsonArray(1) };
        settings[TestRingId] = new JsonObject { ["location"] = 4, ["path"] = "C:\\ext" };
        File.WriteAllText(path, root.ToJsonString());
        _windows.Ids.Add("MSEdge.UserData.Profile1");

        Assert.Empty(Probe(FakeEdgeProfiles.ExtensionId, TestRingId).FindSwitchedOff());
        Assert.Equal(new[] { "Profile 1" }, Probe(FakeEdgeProfiles.ExtensionId).FindSwitchedOff());
    }

    [Fact]
    public void Extension_settings_in_Preferences_count_too()
    {
        _profiles.Write("Profile 1", FakeEdgeProfiles.ExtensionId,
            new JsonObject { ["location"] = 1, ["disable_reasons"] = new JsonArray(1) }, file: "Preferences");
        _windows.Ids.Add("MSEdge.UserData.Profile1");

        Assert.Equal(new[] { "Profile 1" }, Probe().FindSwitchedOff());
    }

    [Fact]
    public void Preferences_that_cannot_be_read_keep_what_they_said_before()
    {
        _profiles.SwitchOff("Profile 1");
        _profiles.SwitchOn("Default");
        _windows.Ids.AddRange(["MSEdge", "MSEdge.UserData.Profile1"]);
        var probe = Probe();
        Assert.Equal(new[] { "Profile 1" }, probe.FindSwitchedOff());

        // Half a file, as a read in the middle of a write would see.
        _profiles.WriteRaw("Profile 1", "Secure Preferences", "{\"extensions\":{\"settings\":{");
        _profiles.WriteRaw("Default", "Secure Preferences", "{\"extensions\":{\"sett");

        Assert.Equal(new[] { "Profile 1" }, probe.FindSwitchedOff());
    }

    [Fact]
    public void No_user_data_folder_finds_nothing()
    {
        _windows.Ids.Add("MSEdge");

        Assert.Empty(Probe().FindSwitchedOff());
    }

    [Theory]
    [InlineData("""{"location":1,"disable_reasons":[]}""", ProfileExtensionState.Enabled)]
    [InlineData("""{"location":4,"path":"C:\\ext"}""", ProfileExtensionState.Enabled)]
    [InlineData("""{"location":1,"disable_reasons":[1]}""", ProfileExtensionState.Disabled)]
    [InlineData("""{"location":1,"disable_reasons":[1,8192]}""", ProfileExtensionState.Disabled)]
    // Older Edge versions: a bit mask, and a state (0 off, 1 on, 2 uninstalled).
    [InlineData("""{"location":1,"disable_reasons":1,"state":0}""", ProfileExtensionState.Disabled)]
    [InlineData("""{"location":1,"state":0}""", ProfileExtensionState.Disabled)]
    [InlineData("""{"location":1,"disable_reasons":0,"state":1}""", ProfileExtensionState.Enabled)]
    [InlineData("""{"location":3,"state":2}""", ProfileExtensionState.Absent)]
    // What Edge leaves behind in some profiles: an entry with nothing in it.
    [InlineData("{}", ProfileExtensionState.Absent)]
    [InlineData("null", ProfileExtensionState.Absent)]
    public void Reads_an_extension_s_settings_entry(string entry, ProfileExtensionState expected)
    {
        using var doc = JsonDocument.Parse(entry);

        Assert.Equal(expected, EdgeOpenProfileExtensionProbe.StateOf(doc.RootElement));
    }

    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\Microsoft\Edge\User Data", "Profile 1", "MSEdge.UserData.Profile1")]
    [InlineData(@"C:\Users\x\AppData\Local\Microsoft\Edge\User Data\", "Profile 12", "MSEdge.UserData.Profile12")]
    [InlineData(@"D:\tmp\udd", "Default", "MSEdge|MSEdge.udd.Default")]
    [InlineData(@"C:\Users\x\AppData\Local\Microsoft\Edge\User Data", "Guest Profile", "MSEdge.UserData.GuestProfile")]
    public void Names_a_profile_s_windows_as_edge_does(string userData, string profile, string expected)
    {
        // As seen on Edge 157: "MSEdge.UserData.Profile1" for Profile 1, and
        // "MSEdge.udd.Default" for the Default profile of a --user-data-dir "udd".
        Assert.Equal(expected.Split('|'), EdgeOpenProfileExtensionProbe.WindowAppIds(userData, profile));
    }

    private sealed class FakeAppIds : IBrowserWindowAppIds
    {
        public List<string> Ids { get; } = new();
        public IReadOnlyList<string> GetWindowAppIds(string processName) => Ids.ToList();
    }
}
