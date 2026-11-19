using FocusAgent.Core.Tamper;

namespace FocusAgent.Core.Tests;

/// <summary>
/// #537: telling the installed site's app an app window belongs to from the
/// package Edge registers for it, whose ID the window carries (shapes measured on
/// Edge 157: <c>localhost-AB457204_ebq0fnkrwaj2c!App</c>).
/// </summary>
public sealed class EdgeWebAppPackagesTests : IDisposable
{
    private const string Family = "localhost-AB457204_ebq0fnkrwaj2c";
    private const string AppId = "abjckpmkhelphnmlbbjeehohmjbopiio";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "anchor-edge-packages-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _lookedUp = new();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private EdgeWebAppPackages Packages(string? defaultUserData = null) =>
        new(family =>
        {
            _lookedUp.Add(family);
            return Path.Combine(_root, family);
        }, defaultUserData ?? @"C:\Users\ada\AppData\Local\Microsoft\Edge\User Data");

    private void Register(string family, string manifest)
    {
        Directory.CreateDirectory(Path.Combine(_root, family));
        File.WriteAllText(Path.Combine(_root, family, "AppxManifest.xml"), manifest);
    }

    [Fact]
    public void An_app_window_s_package_names_the_app_its_user_data_folder_and_the_profile_it_was_installed_in()
    {
        Register(Family, FakeEdgeProfiles.AppManifest("localhost-AB457204", AppId, "Profile 1", @"C:\Temp\edge537\User Data"));

        Assert.Equal(
            new EdgeWebApp(AppId, @"C:\Temp\edge537\User Data", "Profile 1"),
            Packages().Find(Family + "!App"));
    }

    [Fact]
    public void An_app_of_edge_s_own_user_data_folder_names_none_and_comes_from_it()
    {
        Register(Family, FakeEdgeProfiles.AppManifest("localhost-AB457204", AppId, "Default", userDataFolder: null));

        Assert.Equal(
            new EdgeWebApp(AppId, @"C:\Users\ada\AppData\Local\Microsoft\Edge\User Data", "Default"),
            Packages().Find(Family + "!App"));
    }

    [Theory]
    // A browser window's ID: no package to look up.
    [InlineData("MSEdge")]
    [InlineData("MSEdge.UserData.Profile1")]
    [InlineData("")]
    [InlineData("!App")]
    [InlineData("localhost-AB457204_ebq0fnkrwaj2c!")]
    [InlineData("..\\..\\Windows_x!App")]
    public void A_window_id_that_is_no_package_s_is_never_looked_up(string appUserModelId)
    {
        Assert.Null(Packages().Find(appUserModelId));
        Assert.Empty(_lookedUp);
    }

    [Fact]
    public void A_package_that_isn_t_there_or_isn_t_an_edge_app_is_no_app()
    {
        var packages = Packages();

        // Not registered (any more).
        Assert.Null(packages.Find(Family + "!App"));

        // Another app's package, hosted by Edge Beta, and an app the package
        // doesn't have.
        Register("Microsoft.WindowsNotepad_8wekyb3d8bbwe", """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
             <Identity Name="Microsoft.WindowsNotepad" Publisher="CN=Microsoft Corporation" Version="11.0.0.0"/>
             <Applications><Application Id="App" Executable="Notepad\Notepad.exe"/></Applications>
            </Package>
            """);
        Register("localhost-11111111_aaaaaaaaaaaaa", FakeEdgeProfiles.AppManifest(
            "localhost-11111111", AppId, "Default", null, host: "Microsoft.MicrosoftEdge.Beta"));
        Register(Family, FakeEdgeProfiles.AppManifest("localhost-AB457204", AppId, "Default", null));

        Assert.Null(packages.Find("Microsoft.WindowsNotepad_8wekyb3d8bbwe!App"));
        Assert.Null(packages.Find("localhost-11111111_aaaaaaaaaaaaa!App"));
        Assert.Null(packages.Find(Family + "!Other"));
        Assert.NotNull(packages.Find(Family + "!App"));
    }

    [Fact]
    public void A_manifest_is_read_once_and_again_when_it_changes()
    {
        Register(Family, FakeEdgeProfiles.AppManifest("localhost-AB457204", AppId, "Default", null));
        var packages = Packages();

        Assert.Equal("Default", packages.Find(Family + "!App")?.Profile);
        Assert.Equal("Default", packages.Find(Family + "!App")?.Profile);
        Assert.Single(_lookedUp);

        // Removed and installed again from another profile.
        Register(Family, FakeEdgeProfiles.AppManifest("localhost-AB457204", AppId, "Profile 12", null) + "\n");
        Assert.Equal("Profile 12", packages.Find(Family + "!App")?.Profile);
    }

    [Fact]
    public void A_manifest_that_can_t_be_read_is_no_app()
    {
        Register(Family, "<Package><Applications>");

        Assert.Null(Packages().Find(Family + "!App"));
    }
}
