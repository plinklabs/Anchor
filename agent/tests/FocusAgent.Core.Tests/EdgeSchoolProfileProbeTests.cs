using FocusAgent.Core.Auth;
using FocusAgent.Core.Tamper;

namespace FocusAgent.Core.Tests;

/// <summary>
/// #525: finding the Edge profile signed in with the student's school account in
/// Edge's <c>Local State</c>, and telling which profile a browser window belongs to
/// from its AppUserModelID and title (shapes measured on Edge 157).
/// </summary>
public sealed class EdgeSchoolProfileProbeTests : IDisposable
{
    private const string SchoolOid = "28c4a3c2-1f0e-4f55-9a7c-6d3f0a1b2c3d";
    private const string SchoolAddress = "ada.lovelace@student.school.be";
    private static readonly SchoolAccount Ada = new(SchoolAddress, SchoolOid);

    private readonly FakeEdgeProfiles _profiles = new();

    public void Dispose() => _profiles.Dispose();

    private EdgeSchoolProfileProbe Probe() =>
        new(_profiles.UserData,
            new EdgeOpenProfileExtensionProbe(
                _profiles.UserData, new NoWindows(), "msedge", new[] { FakeEdgeProfiles.ExtensionId }));

    [Fact]
    public void The_profile_signed_in_with_the_school_account_s_object_id_is_the_school_profile()
    {
        _profiles.SwitchOn("Default");
        _profiles.SwitchOn("Profile 1");
        _profiles.WriteLocalState(
            ("Default", "ada.personal@outlook.com", null),
            ("Profile 1", SchoolAddress, SchoolOid));

        Assert.Equal(new SchoolProfileReading(SchoolProfileStatus.Found, "Profile 1"), Probe().Find(Ada));
    }

    [Fact]
    public void A_profile_without_an_object_id_is_matched_on_the_address_ignoring_case()
    {
        _profiles.SwitchOn("Profile 2");
        _profiles.WriteLocalState(("Default", "", null), ("Profile 2", "Ada.Lovelace@Student.School.be", null));

        Assert.Equal(new SchoolProfileReading(SchoolProfileStatus.Found, "Profile 2"), Probe().Find(Ada));
    }

    [Fact]
    public void An_object_id_that_differs_is_another_account_even_under_the_same_address()
    {
        _profiles.SwitchOn("Default");
        _profiles.WriteLocalState(("Default", SchoolAddress, "0f9b9a8e-0000-4000-8000-000000000001"));

        Assert.Equal(SchoolProfileStatus.Missing, Probe().Find(Ada).Status);
    }

    [Fact]
    public void Without_a_profile_signed_in_with_the_school_account_it_is_missing()
    {
        _profiles.SwitchOn("Default");
        _profiles.SwitchOn("Profile 1");
        _profiles.WriteLocalState(("Default", "", null), ("Profile 1", "ada.personal@outlook.com", null));

        Assert.Equal(new SchoolProfileReading(SchoolProfileStatus.Missing, null), Probe().Find(Ada));
    }

    [Fact]
    public void A_school_profile_that_never_had_the_extension_is_missing_until_it_has_it()
    {
        // #519: a profile without the extension browses unfiltered.
        _profiles.SwitchOn("Default");
        _profiles.Remove("Profile 1");
        _profiles.WriteLocalState(("Default", "", null), ("Profile 1", SchoolAddress, SchoolOid));
        var probe = Probe();

        Assert.Equal(new SchoolProfileReading(SchoolProfileStatus.Missing, "Profile 1"), probe.Find(Ada));

        _profiles.SwitchOn("Profile 1");
        Assert.Equal(new SchoolProfileReading(SchoolProfileStatus.Found, "Profile 1"), probe.Find(Ada));
    }

    [Fact]
    public void A_school_profile_with_the_extension_switched_off_is_found_the_extension_pause_handles_that()
    {
        _profiles.SwitchOff("Profile 1");
        _profiles.WriteLocalState(("Profile 1", SchoolAddress, SchoolOid));

        Assert.Equal(SchoolProfileStatus.Found, Probe().Find(Ada).Status);
    }

    [Fact]
    public void Without_edge_s_local_state_or_a_signed_in_account_it_can_t_be_told()
    {
        _profiles.SwitchOn("Default");
        var probe = Probe();

        // Edge never ran for this student.
        Assert.Equal(SchoolProfileReading.Unknown, probe.Find(Ada));

        _profiles.WriteLocalState(("Default", SchoolAddress, SchoolOid));
        Assert.Equal(SchoolProfileReading.Unknown, probe.Find(null));
        Assert.Equal(SchoolProfileReading.Unknown, probe.Find(new SchoolAccount("", null)));
        Assert.Equal(SchoolProfileStatus.Found, probe.Find(Ada).Status);
    }

    [Fact]
    public void A_local_state_that_can_t_be_read_for_a_moment_keeps_what_it_said()
    {
        _profiles.SwitchOn("Profile 1");
        _profiles.WriteLocalState(("Profile 1", SchoolAddress, SchoolOid));
        var probe = Probe();
        Assert.Equal(SchoolProfileStatus.Found, probe.Find(Ada).Status);

        // Half-written, as Edge may be writing it right now.
        File.WriteAllText(Path.Combine(_profiles.UserData, "Local State"), """{"profile":{"info_cache":{""");

        Assert.Equal(new SchoolProfileReading(SchoolProfileStatus.Found, "Profile 1"), probe.Find(Ada));
    }

    [Theory]
    // The school profile's ordinary windows: the one place Edge may be used.
    [InlineData("Profile 1", "MSEdge.UserData.Profile1", "Lesson - Profile 1 - Microsoft​ Edge", EdgeWindowProfile.SchoolProfile)]
    // A page that only mentions InPrivate is still an ordinary window.
    [InlineData("Profile 1", "MSEdge.UserData.Profile1", "Browse InPrivate in Microsoft Edge - School - Microsoft​ Edge", EdgeWindowProfile.SchoolProfile)]
    // An InPrivate window carries its profile's ID and says so in its title.
    [InlineData("Profile 1", "MSEdge.UserData.Profile1", "about:blank - [InPrivate] - Microsoft​ Edge", EdgeWindowProfile.InPrivate)]
    [InlineData("Profile 1", "MSEdge", "about:blank - [InPrivate] - Microsoft​ Edge", EdgeWindowProfile.InPrivate)]
    // Another profile, the Default one included.
    [InlineData("Profile 1", "MSEdge", "News - Personal - Microsoft​ Edge", EdgeWindowProfile.OtherProfile)]
    [InlineData("Profile 1", "MSEdge.UserData.Profile7", "News - Profile 2 - Microsoft​ Edge", EdgeWindowProfile.OtherProfile)]
    // Guest, whatever language its title is in.
    [InlineData("Profile 1", "MSEdge.UserData.GuestProfile", "about:blank - [Gast] - Microsoft​ Edge", EdgeWindowProfile.Guest)]
    // The Default profile as the school profile: plain MSEdge.
    [InlineData("Default", "MSEdge", "Lesson - School - Microsoft​ Edge", EdgeWindowProfile.SchoolProfile)]
    [InlineData("Default", "MSEdge.UserData.Default", "Lesson - School - Microsoft​ Edge", EdgeWindowProfile.SchoolProfile)]
    // What the rule can't place is left alone: an app window whose package isn't
    // there (#537), a window without an ID yet, another app.
    [InlineData("Profile 1", "localhost-AB457204_ebq0fnkrwaj2c!App", "Outlook", EdgeWindowProfile.Unknown)]
    [InlineData("Profile 1", null, "Lesson", EdgeWindowProfile.Unknown)]
    [InlineData("Profile 1", "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", "notes.txt", EdgeWindowProfile.Unknown)]
    // Without a school profile, every browser window is outside it.
    [InlineData(null, "MSEdge.UserData.Profile1", "Lesson - Profile 1 - Microsoft​ Edge", EdgeWindowProfile.OtherProfile)]
    public void A_window_belongs_to_the_profile_its_app_id_names(
        string? school, string? appId, string title, EdgeWindowProfile expected)
    {
        Assert.Equal(expected, Probe().Classify(school, appId, title));
    }

    [Fact]
    public void The_rule_pauses_other_profiles_guest_and_inprivate_windows_only()
    {
        Assert.True(EdgeSchoolProfileProbe.IsOutside(EdgeWindowProfile.OtherProfile));
        Assert.True(EdgeSchoolProfileProbe.IsOutside(EdgeWindowProfile.Guest));
        Assert.True(EdgeSchoolProfileProbe.IsOutside(EdgeWindowProfile.InPrivate));
        Assert.False(EdgeSchoolProfileProbe.IsOutside(EdgeWindowProfile.SchoolProfile));
        Assert.False(EdgeSchoolProfileProbe.IsOutside(EdgeWindowProfile.SchoolProfileApp));
        Assert.False(EdgeSchoolProfileProbe.IsOutside(EdgeWindowProfile.Unknown));
    }

    // #537: an installed site's app window carries its package's ID in every
    // profile; Local State lists the profiles the app is installed in.
    private const string TeamsApp = "localhost-AB457204_ebq0fnkrwaj2c";
    private const string GamesApp = "localhost-15D39BB8_ts9w021fep8jm";

    private EdgeSchoolProfileProbe ProbeWithApps() =>
        new(_profiles.UserData,
            new EdgeOpenProfileExtensionProbe(
                _profiles.UserData, new NoWindows(), "msedge", new[] { FakeEdgeProfiles.ExtensionId }),
            apps: _profiles.Apps());

    private void SchoolInProfile1()
    {
        _profiles.SwitchOn("Default");
        _profiles.SwitchOn("Profile 1");
        _profiles.WriteLocalState(("Default", "ada.personal@outlook.com", null), ("Profile 1", SchoolAddress, SchoolOid));
    }

    [Fact]
    public void An_app_window_belongs_to_the_one_profile_its_app_is_installed_in()
    {
        SchoolInProfile1();
        var teams = _profiles.InstallApp(TeamsApp, "abjckpmkhelphnmlbbjeehohmjbopiio", "Profile 1");
        var games = _profiles.InstallApp(GamesApp, "poiahfjabbbejmkahpdcpdgnnfmbakcf", "Default");
        var probe = ProbeWithApps();

        // The school profile's own app stays usable; another profile's is outside it.
        Assert.Equal(EdgeWindowProfile.SchoolProfileApp, probe.Classify("Profile 1", teams, "Teams"));
        Assert.Equal(EdgeWindowProfile.OtherProfile, probe.Classify("Profile 1", games, "Games"));
        // A page title that mentions InPrivate makes no app window InPrivate.
        Assert.Equal(EdgeWindowProfile.OtherProfile, probe.Classify("Profile 1", games, "[InPrivate] games"));
        // Without a school profile, every app window is outside it.
        Assert.Equal(EdgeWindowProfile.OtherProfile, probe.Classify(null, teams, "Teams"));
        // Without the packages, app windows can't be placed (as before #537).
        Assert.Equal(EdgeWindowProfile.Unknown, Probe().Classify("Profile 1", games, "Games"));
    }

    [Fact]
    public void An_app_installed_in_the_school_profile_and_another_is_placed_by_the_profile_its_title_ends_with()
    {
        // Edge 157 ends the windows of an app installed in two profiles with the
        // profile's name: "Anchor537 Measure - Persoonlijk", "... - Profiel 2".
        SchoolInProfile1();
        var outlook = _profiles.InstallApp(TeamsApp, "abjckpmkhelphnmlbbjeehohmjbopiio", "Default", "Profile 1");
        var probe = ProbeWithApps();

        Assert.Equal(EdgeWindowProfile.SchoolProfileApp, probe.Classify("Profile 1", outlook, "Mail - Outlook - School", "School"));
        Assert.Equal(EdgeWindowProfile.OtherProfile, probe.Classify("Profile 1", outlook, "Mail - Outlook - Personal", "School"));
        // Until the name Edge shows for the school profile is known, it can't be told.
        Assert.Equal(EdgeWindowProfile.Unknown, probe.Classify("Profile 1", outlook, "Mail - Outlook - School"));
    }

    [Fact]
    public void Without_local_state_s_list_an_app_belongs_to_the_profile_its_package_names_and_a_deleted_profile_doesn_t_count()
    {
        SchoolInProfile1();
        var teams = _profiles.InstallApp(TeamsApp, "abjckpmkhelphnmlbbjeehohmjbopiio", "Profile 1", "Profile 9");
        var probe = ProbeWithApps();

        // Profile 9 was deleted: it is in no list of Edge's profiles any more.
        Assert.Equal(EdgeWindowProfile.SchoolProfileApp, probe.Classify("Profile 1", teams, "Teams"));

        // A Local State without app_shims: the package's profile.
        _profiles.WriteLocalState(("Default", "ada.personal@outlook.com", null), ("Profile 1", SchoolAddress, SchoolOid));
        File.WriteAllText(
            Path.Combine(_profiles.UserData, "Local State"),
            File.ReadAllText(Path.Combine(_profiles.UserData, "Local State")).Replace("app_shims", "something_else"));
        Assert.Equal(EdgeWindowProfile.SchoolProfileApp, ProbeWithApps().Classify("Profile 1", teams, "Teams"));
        Assert.Equal(EdgeWindowProfile.OtherProfile, ProbeWithApps().Classify("Default", teams, "Teams"));
    }

    [Fact]
    public void An_app_of_another_user_data_folder_is_outside_the_school_profile()
    {
        SchoolInProfile1();
        Directory.CreateDirectory(Path.Combine(_profiles.Packages, TeamsApp));
        // Installed from Edge's own User Data folder, not the one the agent reads.
        File.WriteAllText(
            Path.Combine(_profiles.Packages, TeamsApp, "AppxManifest.xml"),
            FakeEdgeProfiles.AppManifest("localhost-AB457204", "abjckpmkhelphnmlbbjeehohmjbopiio", "Profile 1", null));
        var probe = new EdgeSchoolProfileProbe(
            _profiles.UserData,
            new EdgeOpenProfileExtensionProbe(_profiles.UserData, new NoWindows(), "msedge", new[] { FakeEdgeProfiles.ExtensionId }),
            apps: _profiles.Apps(defaultUserDataFolder: @"C:\Users\ada\AppData\Local\Microsoft\Edge\User Data"));

        Assert.Equal(EdgeWindowProfile.OtherProfile, probe.Classify("Profile 1", TeamsApp + "!App", "Teams"));

        // The same app, of the User Data folder the agent reads when that is Edge's own.
        var own = new EdgeSchoolProfileProbe(
            _profiles.UserData + "\\",
            new EdgeOpenProfileExtensionProbe(_profiles.UserData, new NoWindows(), "msedge", new[] { FakeEdgeProfiles.ExtensionId }),
            apps: _profiles.Apps(defaultUserDataFolder: _profiles.UserData));
        Assert.Equal(EdgeWindowProfile.SchoolProfileApp, own.Classify("Profile 1", TeamsApp + "!App", "Teams"));
    }

    [Fact]
    public void The_name_edge_shows_for_the_school_profile_comes_from_one_of_its_ordinary_browser_windows()
    {
        var probe = Probe();
        var windows = new[]
        {
            new BrowserWindowInfo(1, "MSEdge", "News - Personal - Microsoft​ Edge", false),
            new BrowserWindowInfo(2, "MSEdge.UserData.Profile1", "about:blank - [InPrivate] - Microsoft​ Edge", false),
            new BrowserWindowInfo(3, TeamsApp + "!App", "Teams - School", false),
            new BrowserWindowInfo(4, "MSEdge.UserData.Profile1", "Lesson - Week 3 - School - Microsoft​ Edge", true),
        };

        Assert.Equal("School", probe.SchoolProfileName("Profile 1", windows));
        Assert.Equal("Personal", probe.SchoolProfileName("Default", windows));
        Assert.Null(probe.SchoolProfileName("Profile 1", windows.Take(3)));
        Assert.Null(probe.SchoolProfileName(null, windows));
        // A title without the profile's name has none to give.
        Assert.Null(probe.SchoolProfileName("Profile 1", [new BrowserWindowInfo(5, "MSEdge.UserData.Profile1", "Microsoft​ Edge", false)]));
    }

    private sealed class NoWindows : IBrowserWindowAppIds
    {
        public IReadOnlyList<string> GetWindowAppIds(string processName) => Array.Empty<string>();
    }
}
