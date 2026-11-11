using System.Reflection;

namespace FocusAgent.Core.Diagnostics;

/// <summary>
/// The agent's version as every surface shows it (#423): the main window, the tray
/// menu, the crash dialog, the startup log line, the log upload and the
/// <c>/status</c> endpoint all read <see cref="Current"/>, so they can't disagree.
///
/// <para>
/// The number comes from the single version source, <c>agent/Directory.Build.props</c>
/// (#208): MSBuild stamps its &lt;VersionPrefix&gt; into every agent assembly's
/// InformationalVersion, which may carry a "+&lt;commit&gt;" SourceLink suffix. Only the
/// SemVer before the "+" is shown. FocusAgent.Core inherits the same props as
/// FocusAgent.App, so its own stamp is the agent's, and reading it here keeps the
/// result the same in the app, the witness host and the unit tests.
/// </para>
/// </summary>
public static class AgentVersion
{
    /// <summary>Shown when an assembly carries no version at all.</summary>
    public const string Unknown = "0.0.0";

    /// <summary>The running agent's version, e.g. "0.3.1". Never throws, never empty.</summary>
    public static string Current { get; } = Of(typeof(AgentVersion).Assembly);

    /// <summary>
    /// The version stamped on <paramref name="assembly"/>, read the way
    /// <see cref="Resolve"/> describes. Never throws: the crash dialog reads it too,
    /// at a moment when anything may be broken.
    /// </summary>
    public static string Of(Assembly assembly)
    {
        try
        {
            return Resolve(
                assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                assembly.GetName().Version);
        }
        catch
        {
            return Unknown;
        }
    }

    /// <summary>
    /// The InformationalVersion without its "+&lt;commit&gt;" suffix; when there is none
    /// (or only a suffix), the assembly version without a zero revision ("0.3.1.0"
    /// shows as "0.3.1"); and <see cref="Unknown"/> when there is neither.
    /// </summary>
    public static string Resolve(string? informationalVersion, Version? assemblyVersion)
    {
        var semver = informationalVersion?.Split('+', 2)[0].Trim();
        if (!string.IsNullOrEmpty(semver))
            return semver;

        if (assemblyVersion is not null)
            return assemblyVersion.Build >= 0 && assemblyVersion.Revision <= 0
                ? assemblyVersion.ToString(3)
                : assemblyVersion.ToString();

        return Unknown;
    }
}
