using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusAgent.Core.Extension;

/// <summary>
/// Whether Anchor's extension is installed in an Edge profile, read from disk so
/// Edge doesn't have to be running (#488). Edge unpacks every extension it has
/// installed, force-installed or added from the store, to
/// <c>User Data\&lt;profile&gt;\Extensions\&lt;id&gt;\&lt;version&gt;\manifest.json</c>,
/// and deletes that folder when the extension is removed. The witness link only
/// says the extension is <em>running</em>, which it can't be before Edge is opened:
/// right after sign-in the agent is up long before Edge is, so this is the
/// "installed" signal and the witness stays the "running" one.
///
/// Reading only, without elevation: the folders are the student's own.
/// </summary>
public sealed class EdgeProfileExtensionProbe
{
    // Stable, Beta, Dev and Canary: each channel keeps its own User Data folder,
    // and all of them honour the force-install policy.
    private static readonly string[] Channels = ["Edge", "Edge Beta", "Edge Dev", "Edge SxS"];

    private readonly IReadOnlyList<string> _userDataFolders;
    private readonly string _extensionId;
    private readonly ILogger<EdgeProfileExtensionProbe> _log;

    /// <param name="userDataFolders">The Edge <c>User Data</c> folders to look in.</param>
    /// <param name="extensionId">The extension to look for; Anchor's by default.</param>
    public EdgeProfileExtensionProbe(
        IEnumerable<string> userDataFolders,
        string extensionId = EdgeExtensionPolicy.ExtensionId,
        ILogger<EdgeProfileExtensionProbe>? log = null)
    {
        _userDataFolders = userDataFolders.ToList();
        _extensionId = extensionId;
        _log = log ?? NullLogger<EdgeProfileExtensionProbe>.Instance;
    }

    /// <summary>
    /// The <c>User Data</c> folder of every Edge channel under
    /// <paramref name="localAppData"/> (<c>%LOCALAPPDATA%</c>), whether or not that
    /// channel is installed.
    /// </summary>
    public static IReadOnlyList<string> UserDataFolders(string localAppData) =>
        Channels.Select(channel => Path.Combine(localAppData, "Microsoft", channel, "User Data")).ToList();

    /// <summary>
    /// Where the extension is installed, as <c>&lt;channel folder&gt;\&lt;profile&gt;</c>
    /// (e.g. <c>Edge\Default</c>), or null when no profile has it. Only folder names,
    /// so the log line never carries the student's user name. A folder that can't
    /// be read counts as not having it.
    /// </summary>
    public string? FindInstalledProfile()
    {
        foreach (var userData in _userDataFolders)
        {
            try
            {
                if (!Directory.Exists(userData)) continue;
                foreach (var profile in Directory.EnumerateDirectories(userData))
                {
                    if (HasUnpackedVersion(Path.Combine(profile, "Extensions", _extensionId)))
                        return $@"{Path.GetFileName(Path.GetDirectoryName(userData.TrimEnd('\\', '/')))}\{Path.GetFileName(profile)}";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Reading Edge's profiles failed; treating them as not having the extension.");
            }
        }
        return null;
    }

    // A version folder with its manifest: what Edge leaves once an install has
    // finished unpacking. An empty <id> folder is not an install.
    private static bool HasUnpackedVersion(string extensionFolder) =>
        Directory.Exists(extensionFolder) &&
        Directory.EnumerateDirectories(extensionFolder)
            .Any(version => File.Exists(Path.Combine(version, "manifest.json")));
}
