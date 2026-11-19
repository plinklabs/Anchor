using System.Runtime.Versioning;
using FocusAgent.Native;
using Windows.Management.Deployment;

namespace FocusAgent.Native.Tests;

/// <summary>
/// #537: the folder of a package registered for the user, by its family, where
/// Edge's installed-site apps keep the manifest that names them. Checked against
/// what Windows' package manager says of a package this user has.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public class InstalledPackagesTests
{
    [Fact]
    public void Finds_the_folder_of_a_package_registered_for_the_user()
    {
        // A family with one package of the user's (no resource or framework
        // packages beside it), whatever this machine has.
        var package = new PackageManager().FindPackagesForUser(string.Empty)
            .Where(p => !p.IsFramework && !p.IsResourcePackage && !p.IsBundle)
            .GroupBy(p => p.Id.FamilyName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1)
            .Select(g => g.Single())
            .FirstOrDefault(p => Location(p) is { } path && File.Exists(Path.Combine(path, "AppxManifest.xml")));
        Assert.True(package is not null, "No package is registered for this user to look up.");

        var folder = InstalledPackages.FolderOf(package!.Id.FamilyName);

        Assert.NotNull(folder);
        Assert.Equal(
            Path.GetFullPath(Location(package)!).TrimEnd('\\'),
            Path.GetFullPath(folder).TrimEnd('\\'),
            ignoreCase: true);
    }

    [Fact]
    public void Finds_none_for_a_family_that_isn_t_registered()
    {
        Assert.Null(InstalledPackages.FolderOf("localhost-00000000_" + "anchornosuch1"));
    }

    private static string? Location(Windows.ApplicationModel.Package package)
    {
        try
        {
            return package.InstalledLocation?.Path;
        }
        catch (Exception)
        {
            // A package whose folder is gone or can't be opened.
            return null;
        }
    }
}
