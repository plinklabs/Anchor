using FocusAgent.Core.Extension;

namespace FocusAgent.Core.Tests;

/// <summary>
/// The "installed" signal (#488): Anchor's extension unpacked in an Edge profile,
/// found without Edge running. Each spec builds a throwaway <c>User Data</c> tree.
/// </summary>
public sealed class EdgeProfileExtensionProbeTests : IDisposable
{
    private const string OtherId = "someotherextensionidaaaaaaaaaaaa";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"anchor-edge-probe-{Guid.NewGuid():N}");

    private string UserData() => Path.Combine(_root, "Edge", "User Data");

    private void Unpack(string userData, string profile, string id = EdgeExtensionPolicy.ExtensionId, bool manifest = true)
    {
        var version = Path.Combine(userData, profile, "Extensions", id, "0.4.3_0");
        Directory.CreateDirectory(version);
        if (manifest)
            File.WriteAllText(Path.Combine(version, "manifest.json"), "{}");
    }

    [Theory]
    [InlineData("Default")]
    [InlineData("Profile 1")]
    public void Finds_the_extension_unpacked_in_a_profile(string profile)
    {
        Directory.CreateDirectory(Path.Combine(UserData(), "Default"));
        Unpack(UserData(), profile);

        var found = new EdgeProfileExtensionProbe([UserData()]).FindInstalledProfile();

        Assert.Equal($@"Edge\{profile}", found);
    }

    [Fact]
    public void Another_extension_is_not_ours()
    {
        Unpack(UserData(), "Default", OtherId);

        Assert.Null(new EdgeProfileExtensionProbe([UserData()]).FindInstalledProfile());
    }

    [Fact]
    public void A_folder_without_an_unpacked_version_is_not_an_install()
    {
        // What a half-finished install or a removal can leave behind.
        Directory.CreateDirectory(Path.Combine(UserData(), "Default", "Extensions", EdgeExtensionPolicy.ExtensionId));
        Unpack(UserData(), "Profile 1", manifest: false);

        Assert.Null(new EdgeProfileExtensionProbe([UserData()]).FindInstalledProfile());
    }

    [Fact]
    public void An_Edge_that_was_never_started_has_no_extension()
    {
        Assert.Null(new EdgeProfileExtensionProbe([UserData()]).FindInstalledProfile());
    }

    [Fact]
    public void Looks_in_every_channel_under_LocalAppData()
    {
        // Stable Edge has run but doesn't have it; Edge Beta does; Dev and Canary
        // aren't installed at all.
        var localAppData = Path.Combine(_root, "Local");
        var microsoft = Path.Combine(localAppData, "Microsoft");
        Directory.CreateDirectory(Path.Combine(microsoft, "Edge", "User Data", "Default"));
        Unpack(Path.Combine(microsoft, "Edge Beta", "User Data"), "Default");

        var found = new EdgeProfileExtensionProbe(EdgeProfileExtensionProbe.UserDataFolders(localAppData))
            .FindInstalledProfile();

        Assert.Equal(@"Edge Beta\Default", found);
    }

    [Fact]
    public void The_channels_live_under_LocalAppData_Microsoft()
    {
        var local = Path.Combine("C:", "Users", "student", "AppData", "Local");

        Assert.Equal(
            new[] { "Edge", "Edge Beta", "Edge Dev", "Edge SxS" }
                .Select(channel => Path.Combine(local, "Microsoft", channel, "User Data")),
            EdgeProfileExtensionProbe.UserDataFolders(local));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
