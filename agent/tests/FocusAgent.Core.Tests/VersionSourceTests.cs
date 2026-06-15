using System.Reflection;
using System.Xml.Linq;
using FocusAgent.Core.Settings;

namespace FocusAgent.Core.Tests;

/// <summary>
/// Unit coverage for the #208 single-version-source contract.
///
/// <para>
/// <c>agent/Directory.Build.props</c> declares one &lt;VersionPrefix&gt; that
/// MSBuild auto-imports into every agent project. These tests lock the two ways
/// that one number must stay authoritative:
/// </para>
/// <list type="number">
///   <item>It actually stamps the built agent assemblies — the FocusAgent.Core
///   assembly (same Directory.Build.props as FocusAgent.App) must carry that
///   version. This is the unit-speed proof that the source flows into the build;
///   the IntegrationTests/AgentVersionTests spec proves the same for the running
///   exe end-to-end.</item>
///   <item>The MSIX <c>Package.appxmanifest</c> &lt;Identity Version&gt; (the one
///   version the packaged build can't derive from &lt;VersionPrefix&gt;
///   automatically) stays in lockstep, so the packaged build can't ship a stale
///   version while the unpackaged Velopack build moves.</item>
/// </list>
/// </summary>
public class VersionSourceTests
{
    private static string RepoRoot => FindRepoRoot();

    private static string SingleVersionSource()
    {
        var propsPath = Path.Combine(RepoRoot, "agent", "Directory.Build.props");
        Assert.True(File.Exists(propsPath), $"Missing single version source at {propsPath}.");
        var doc = XDocument.Load(propsPath);
        var version = doc.Descendants("VersionPrefix").FirstOrDefault()?.Value
            ?? doc.Descendants("Version").FirstOrDefault()?.Value;
        Assert.False(
            string.IsNullOrWhiteSpace(version),
            "Directory.Build.props declares no <VersionPrefix>/<Version>.");
        return version!.Trim();
    }

    [Fact]
    public void SingleVersionSource_StampsTheBuiltAgentAssemblies()
    {
        var expected = SingleVersionSource();

        // FocusAgent.Core inherits the same agent/Directory.Build.props as the app,
        // so its assembly carries the version stamped from that one source.
        var informational = typeof(BackendSettings).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        Assert.False(
            string.IsNullOrWhiteSpace(informational),
            "FocusAgent.Core assembly has no InformationalVersion — the version source did not flow into the build.");

        // InformationalVersion is "<VersionPrefix>" optionally plus a "+<sha>"
        // SourceLink suffix; the SemVer it starts with must be the single source.
        var semver = informational!.Split('+', 2)[0];
        Assert.Equal(expected, semver);
    }

    [Fact]
    public void MsixPackageManifest_VersionMatchesTheSingleSource()
    {
        var expected = SingleVersionSource();

        var manifestPath = Path.Combine(
            RepoRoot, "agent", "src", "FocusAgent.App", "Package.appxmanifest");
        Assert.True(File.Exists(manifestPath), $"Missing appxmanifest at {manifestPath}.");

        var doc = XDocument.Load(manifestPath);
        XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        var identityVersion = doc.Root!
            .Element(ns + "Identity")?
            .Attribute("Version")?.Value;

        Assert.False(
            string.IsNullOrWhiteSpace(identityVersion),
            "Package.appxmanifest has no <Identity Version>.");

        // The appxmanifest version is the four-part "<VersionPrefix>.0" form.
        Assert.Equal($"{expected}.0", identityVersion);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, "agent")) &&
                Directory.Exists(Path.Combine(dir, "backend")))
            {
                return dir;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException(
            "Could not locate the repo root (no ancestor dir contains both 'agent' and 'backend').");
    }
}
