using System.Reflection;
using System.Reflection.Emit;
using System.Xml.Linq;
using FocusAgent.Core.Diagnostics;

namespace FocusAgent.Core.Tests;

/// <summary>
/// Unit coverage for <see cref="AgentVersion"/> (#423), the one helper the main
/// window, the tray menu, the crash dialog and /status read the agent's version
/// through. The IntegrationTests' AgentVersionShownTests prove the built exe puts it
/// on the main window and in the tray menu.
/// </summary>
public class AgentVersionTests
{
    [Theory]
    [InlineData("0.3.1+4f2c9a1e0b7d", "0.3.1")]
    [InlineData("0.3.1", "0.3.1")]
    [InlineData("0.4.0-beta.2+4f2c9a1", "0.4.0-beta.2")]
    [InlineData(" 0.3.1 +4f2c9a1", "0.3.1")]
    public void Resolve_shows_the_informational_version_without_its_commit(string informational, string expected) =>
        Assert.Equal(expected, AgentVersion.Resolve(informational, new Version(9, 9, 9, 9)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+4f2c9a1")]
    public void Resolve_falls_back_to_the_assembly_version(string? informational) =>
        Assert.Equal("0.3.1", AgentVersion.Resolve(informational, new Version(0, 3, 1, 0)));

    [Fact]
    public void Resolve_keeps_a_non_zero_revision_and_a_two_part_version() =>
        Assert.Equal(
            ["1.2.3.4", "1.2.3", "1.2"],
            new[] { new Version(1, 2, 3, 4), new Version(1, 2, 3), new Version(1, 2) }
                .Select(v => AgentVersion.Resolve(null, v)));

    [Fact]
    public void Resolve_with_no_version_at_all_is_unknown() =>
        Assert.Equal(AgentVersion.Unknown, AgentVersion.Resolve(null, null));

    [Fact]
    public void Of_an_assembly_reads_its_informational_version_without_the_commit()
    {
        var assembly = DynamicAssembly(new Version(9, 9, 9, 0), informational: "1.4.2+4f2c9a1e0b7d");
        Assert.Equal("1.4.2", AgentVersion.Of(assembly));
    }

    [Fact]
    public void Of_an_assembly_without_an_informational_version_uses_its_assembly_version()
    {
        var assembly = DynamicAssembly(new Version(2, 5, 7, 0), informational: null);
        Assert.Equal("2.5.7", AgentVersion.Of(assembly));
    }

    // An in-memory assembly stamped with only what the test gives it.
    private static Assembly DynamicAssembly(Version version, string? informational)
    {
        var builder = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"AgentVersionProbe{Guid.NewGuid():N}") { Version = version },
            AssemblyBuilderAccess.Run);
        if (informational is not null)
        {
            builder.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!,
                [informational]));
        }
        return builder;
    }

    [Fact]
    public void Current_is_the_single_version_source()
    {
        // The committed <VersionPrefix> in agent/Directory.Build.props (#208): what
        // the main window, the tray menu and /status all show.
        Assert.Equal(VersionPrefix(), AgentVersion.Current);
    }

    private static string VersionPrefix()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "agent", "Directory.Build.props")))
            dir = Directory.GetParent(dir)?.FullName;
        Assert.NotNull(dir);

        var doc = XDocument.Load(Path.Combine(dir!, "agent", "Directory.Build.props"));
        var prefix = doc.Descendants("VersionPrefix").FirstOrDefault()?.Value;
        Assert.False(string.IsNullOrWhiteSpace(prefix), "Directory.Build.props declares no <VersionPrefix>.");
        return prefix!.Trim();
    }
}
