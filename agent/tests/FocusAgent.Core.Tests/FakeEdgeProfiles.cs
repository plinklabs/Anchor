using System.Text.Json;
using System.Text.Json.Nodes;
using FocusAgent.Core.Tamper;

namespace FocusAgent.Core.Tests;

/// <summary>
/// An Edge <c>User Data</c> folder of the test's own, with profiles whose
/// <c>Secure Preferences</c> say what Edge's do about one extension (#518): on,
/// switched off (<c>disable_reasons</c>), or not there. Shaped after the files of
/// Edge 157. The folder is only created once a profile is written.
/// </summary>
internal sealed class FakeEdgeProfiles : IDisposable
{
    public const string ExtensionId = "dnkimhodjfogjibnbbfdjdapgmmiojio";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "anchor-edge-profiles-" + Guid.NewGuid().ToString("N"));

    /// <summary>The <c>User Data</c> folder, named as Edge's is.</summary>
    public string UserData => Path.Combine(_root, "User Data");

    public string ProfileFolder(string profile) => Path.Combine(UserData, profile);

    /// <summary>The extension installed and on in <paramref name="profile"/>.</summary>
    public void SwitchOn(string profile, string id = ExtensionId) =>
        Write(profile, id, new JsonObject { ["location"] = 1, ["path"] = $"{id}\\0.4.4_0", ["disable_reasons"] = new JsonArray() });

    /// <summary>The extension installed and switched off by the user (reason 1).</summary>
    public void SwitchOff(string profile, string id = ExtensionId) =>
        Write(profile, id, new JsonObject { ["location"] = 1, ["path"] = $"{id}\\0.4.4_0", ["disable_reasons"] = new JsonArray(1) });

    /// <summary>The extension removed from (or never in) <paramref name="profile"/>.</summary>
    public void Remove(string profile, string id = ExtensionId) => Write(profile, id, null);

    /// <summary>Writes <paramref name="entry"/> as the extension's settings, in the file Edge uses.</summary>
    public void Write(string profile, string id, JsonObject? entry, string file = "Secure Preferences")
    {
        var settings = new JsonObject
        {
            // Another extension, as every real profile has.
            ["ghbmnnjooekpmoecnnnilnnbdlolhkhi"] = new JsonObject { ["location"] = 5, ["disable_reasons"] = new JsonArray(1) },
        };
        if (entry is not null) settings[id] = entry;
        WriteRaw(profile, file, new JsonObject { ["extensions"] = new JsonObject { ["settings"] = settings } }.ToJsonString());
        if (!File.Exists(Path.Combine(ProfileFolder(profile), "Preferences")))
            WriteRaw(profile, "Preferences", """{"extensions":{"alerts":{"initialized":true}}}""");
    }

    /// <summary>
    /// Edge's <c>Local State</c>, with the account each profile is signed in with
    /// (#525), shaped as Edge 157 writes it: <c>profile.info_cache.&lt;folder&gt;</c>
    /// with <c>user_name</c> (empty when not signed in) and, for a work or school
    /// account, <c>edge_account_oid</c> (empty for a personal account).
    /// </summary>
    public void WriteLocalState(params (string Profile, string? UserName, string? ObjectId)[] profiles)
    {
        _profiles = profiles;
        var cache = new JsonObject();
        foreach (var (profile, userName, objectId) in profiles)
            cache[profile] = new JsonObject
            {
                ["name"] = profile,
                ["user_name"] = userName ?? "",
                ["edge_account_oid"] = objectId ?? "",
                ["edge_account_type"] = objectId is null ? 1 : 2,
                ["is_using_default_name"] = true,
            };
        Directory.CreateDirectory(UserData);
        var localState = new JsonObject
        {
            ["profile"] = new JsonObject { ["info_cache"] = cache, ["last_used"] = profiles.FirstOrDefault().Profile },
        };
        // The profiles each installed site's app is installed in (#537).
        if (_appProfiles.Count > 0)
        {
            var shims = new JsonObject();
            foreach (var (appId, installed) in _appProfiles)
                shims[appId] = new JsonObject
                {
                    ["installed_profiles"] = new JsonArray(installed.Select(p => (JsonNode?)p).ToArray()),
                    ["last_active_profiles"] = new JsonArray((JsonNode?)installed[^1]),
                };
            localState["app_shims"] = shims;
        }
        // In one move, as Edge replaces the file; and a different size from the
        // last one, so a reader that goes by write time and size sees the change
        // within the file system's time resolution.
        var temp = Path.Combine(UserData, "Local State.tmp");
        File.WriteAllText(temp, localState.ToJsonString() + new string(' ', ++_localStateWrites));
        File.Move(temp, Path.Combine(UserData, "Local State"), overwrite: true);
    }

    private int _localStateWrites;
    private (string Profile, string? UserName, string? ObjectId)[] _profiles = [];
    private readonly Dictionary<string, string[]> _appProfiles = new();

    /// <summary>Where <see cref="InstallApp"/> registers the apps' packages, one folder per package family.</summary>
    public string Packages => Path.Combine(_root, "Packages");

    /// <summary>Reads the packages <see cref="InstallApp"/> registered, as the agent reads Windows'.</summary>
    public EdgeWebAppPackages Apps(string? defaultUserDataFolder = null) =>
        new(family => Path.Combine(Packages, family), defaultUserDataFolder);

    /// <summary>
    /// A site installed as an app (#537), as Edge 157 does it: a package of its own
    /// (<paramref name="family"/>) whose manifest names the app, this
    /// <c>User Data</c> folder and <paramref name="profile"/>, the profile it was
    /// installed in first; and <c>Local State</c> listing it under every profile it
    /// is installed in, <paramref name="alsoIn"/> too (written again, with the
    /// profiles of the last <see cref="WriteLocalState"/>). Returns the
    /// AppUserModelID its windows carry, in every one of those profiles.
    /// </summary>
    public string InstallApp(string family, string appId, string profile, params string[] alsoIn)
    {
        var folder = Path.Combine(Packages, family);
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "AppxManifest.xml"),
            AppManifest(family[..family.IndexOf('_')], appId, profile, UserData));
        _appProfiles[appId] = [profile, .. alsoIn];
        WriteLocalState(_profiles);
        return family + "!App";
    }

    /// <summary>
    /// The <c>AppxManifest.xml</c> Edge 157 writes for a site installed as an app,
    /// shaped after a measured one: hosted by Edge, its parameters naming the app
    /// and, unless null, the <c>User Data</c> folder, and its
    /// <c>com.ms.webapp.internals</c> extension the profile.
    /// </summary>
    public static string AppManifest(
        string packageName, string appId, string profile, string? userDataFolder,
        string host = EdgeWebAppPackages.EdgeHostPackage)
    {
        var userData = userDataFolder is null ? "" : $" --user-data-dir=&quot;{userDataFolder}&quot;";
        var parameters = $"--app-id={appId} --ip-edge-aumid=Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe!MSEDGE{userData}";
        return $"""
            <?xml version="1.0"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:mp="http://schemas.microsoft.com/appx/2014/phone/manifest" xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10" xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3" xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10" xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities" xmlns:rescap3="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities/3" xmlns:desktop2="http://schemas.microsoft.com/appx/manifest/desktop/windows10/2" IgnorableNamespaces="uap uap3 rescap desktop2">
             <Identity Name="{packageName}" Publisher="CN=localhost:18537, OID.2.25.311729368913984317654407730594956997722=1" Version="1.0.0.0" ProcessorArchitecture="neutral"/>
             <Properties>
              <DisplayName>Outlook</DisplayName>
              <PublisherDisplayName>localhost:18537</PublisherDisplayName>
              <Logo>Images\StoreLogo.png</Logo>
             </Properties>
             <Dependencies>
              <TargetDeviceFamily Name="Windows.Universal" MinVersion="10.0.19041.0" MaxVersionTested="10.0.19041.0"/>
              <uap10:HostRuntimeDependency Name="{host}" Publisher="CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US" MinVersion="1.0.0.0"/>
             </Dependencies>
             <Resources>
              <Resource Language="EN-US"/>
             </Resources>
             <Applications>
              <Application Id="App" uap10:HostId="PWA" uap10:Parameters="{parameters}">
               <uap:VisualElements DisplayName="Outlook" Description="Outlook" BackgroundColor="transparent" Square150x150Logo="Images\Square150x150Logo.png" Square44x44Logo="Images\Square44x44Logo.png"/>
               <Extensions>
                <uap3:Extension Category="windows.appExtension">
                 <uap3:AppExtension Name="com.ms.webapp.internals.4" Id="{packageName}" PublicFolder="Public" DisplayName="Edge WebApp Internals" Description="parameters?{parameters};profile-directory?{profile};start-url?http://localhost:18537/"/>
                </uap3:Extension>
               </Extensions>
              </Application>
             </Applications>
             <Capabilities>
              <rescap:Capability Name="runFullTrust"/>
              <rescap:Capability Name="packageManagement"/>
             </Capabilities>
            </Package>
            """;
    }

    public void WriteRaw(string profile, string file, string content)
    {
        Directory.CreateDirectory(ProfileFolder(profile));
        File.WriteAllText(Path.Combine(ProfileFolder(profile), file), content);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
