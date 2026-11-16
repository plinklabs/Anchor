using System.Text.Json;
using System.Text.Json.Nodes;

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
