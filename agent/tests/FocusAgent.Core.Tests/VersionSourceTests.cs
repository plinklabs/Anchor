using System.Reflection;
using System.Xml.Linq;
using FocusAgent.Core.Settings;

namespace FocusAgent.Core.Tests;

/// <summary>
/// Unit coverage for the #208 single-version-source contract.
///
/// <para>
/// <c>agent/Directory.Build.props</c> declares one &lt;VersionPrefix&gt; that
/// MSBuild auto-imports into every agent project. This test locks that the one
/// number actually stamps the built agent assemblies — the FocusAgent.Core
/// assembly (same Directory.Build.props as FocusAgent.App) must carry that
/// version. This is the unit-speed proof that the source flows into the build;
/// the IntegrationTests/AgentVersionTests spec proves the same for the running
/// exe end-to-end.
/// </para>
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
