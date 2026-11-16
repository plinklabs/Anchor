using System.Text.Json;
using FocusAgent.Core.Extension;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusAgent.Core.Tamper;

/// <summary>
/// The explicit AppUserModelID of the browser's open windows. Edge gives every
/// browser window the ID of the profile it belongs to
/// (<c>MSEdge.UserData.Profile1</c>; plain <c>MSEdge</c> for the Default profile),
/// which is how Windows keeps one taskbar button per profile, and how the agent
/// tells which profiles have a window open (#518).
/// </summary>
public interface IBrowserWindowAppIds
{
    /// <summary>
    /// The AppUserModelID of every top-level window of <paramref name="processName"/>
    /// that carries one, minimized windows included. Best-effort: a window that
    /// can't be read is left out.
    /// </summary>
    IReadOnlyList<string> GetWindowAppIds(string processName);
}

/// <summary>Finds the Edge profiles that are open without Anchor's extension (#518).</summary>
public interface IOpenProfileExtensionProbe
{
    /// <summary>
    /// The profiles that have a browser window open and in which the extension is
    /// switched off, or was removed since this probe first saw it there. Empty
    /// when every open profile has it (or never had it).
    /// </summary>
    IReadOnlyList<string> FindSwitchedOff();
}

/// <summary>What an Edge profile's preferences say about Anchor's extension.</summary>
public enum ProfileExtensionState
{
    /// <summary>Not installed in this profile.</summary>
    Absent,

    /// <summary>Installed and on.</summary>
    Enabled,

    /// <summary>Installed and switched off.</summary>
    Disabled,

    /// <summary>The preferences couldn't be read.</summary>
    Unknown,
}

/// <summary>
/// Tells "this profile was closed" from "the extension was switched off in a
/// profile that is still open" (#518), which the witness link alone can't: the
/// link drops the same way for both, and a profile can even keep its extension
/// running with no window at all. Two things Edge keeps outside the link do tell:
/// <list type="bullet">
/// <item>Which profiles have a window open: each browser window carries its
/// profile's AppUserModelID (<see cref="IBrowserWindowAppIds"/>).</item>
/// <item>Whether the extension is on in a profile: its
/// <c>Secure Preferences</c> (or <c>Preferences</c>) file has the extension under
/// <c>extensions.settings.&lt;id&gt;</c>, with a non-empty <c>disable_reasons</c>
/// while it is switched off, and no entry once it is removed.</item>
/// </list>
/// A profile that is open with the extension switched off or removed is what gets
/// reported. A closed profile has no window, so its link dropping reports nothing.
///
/// Edge writes a profile's preferences 10 seconds after a change (measured on
/// Edge 157; it is Chromium's commit interval), so what this reads lags by that
/// much both ways; <see cref="OpenProfileExtensionWatch"/> allows for it.
///
/// "Removed" needs a memory: a profile that never had the extension has no entry
/// either. The probe remembers each profile it saw the extension in, on or off,
/// for as long as the agent runs.
///
/// Reading only, without elevation: the files are the student's own. Covers the
/// stable Edge channel, whose windows are named <c>MSEdge…</c>.
/// </summary>
public sealed class EdgeOpenProfileExtensionProbe : IOpenProfileExtensionProbe
{
    /// <summary>The start of every AppUserModelID of a stable Edge browser window.</summary>
    public const string BrowserAppId = "MSEdge";

    private static readonly string[] PreferenceFiles = ["Secure Preferences", "Preferences"];

    private readonly string _userDataFolder;
    private readonly IBrowserWindowAppIds _windows;
    private readonly string _browserProcessName;
    private readonly IReadOnlyList<string> _extensionIds;
    private readonly ILogger<EdgeOpenProfileExtensionProbe> _log;
    private readonly object _gate = new();
    // Per preferences file: what it said when last read, and the write time and
    // size it had then, so an unchanged file is not parsed again.
    private readonly Dictionary<string, (DateTime Written, long Length, ProfileExtensionState State)> _files =
        new(StringComparer.OrdinalIgnoreCase);
    // Profiles the extension has been seen in, on or off.
    private readonly HashSet<string> _hadExtension = new(StringComparer.OrdinalIgnoreCase);
    // The last state that could be read, for a moment the files can't be.
    private readonly Dictionary<string, ProfileExtensionState> _lastKnown = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="userDataFolder">Edge's <c>User Data</c> folder.</param>
    /// <param name="windows">Reads the open browser windows' AppUserModelIDs.</param>
    /// <param name="browserProcessName">The browser's process name (<c>msedge</c>).</param>
    /// <param name="extensionIds">
    /// The extension IDs that are Anchor's (<see cref="AnchorExtensionIds.Configured"/>
    /// by default); a profile with any of them on has the extension.
    /// </param>
    public EdgeOpenProfileExtensionProbe(
        string userDataFolder,
        IBrowserWindowAppIds windows,
        string browserProcessName,
        IEnumerable<string>? extensionIds = null,
        ILogger<EdgeOpenProfileExtensionProbe>? log = null)
    {
        _userDataFolder = userDataFolder.TrimEnd('\\', '/');
        _windows = windows;
        _browserProcessName = browserProcessName;
        _extensionIds = (extensionIds ?? AnchorExtensionIds.Configured.All).ToList();
        _log = log ?? NullLogger<EdgeOpenProfileExtensionProbe>.Instance;
    }

    public IReadOnlyList<string> FindSwitchedOff()
    {
        lock (_gate)
        {
            var found = new List<string>();
            try
            {
                if (!Directory.Exists(_userDataFolder)) return found;

                HashSet<string>? open = null;
                foreach (var profileFolder in Directory.EnumerateDirectories(_userDataFolder))
                {
                    if (!PreferenceFiles.Any(file => File.Exists(Path.Combine(profileFolder, file)))) continue;
                    var profile = Path.GetFileName(profileFolder);

                    var state = ReadState(profileFolder);
                    if (state == ProfileExtensionState.Unknown)
                        state = _lastKnown.GetValueOrDefault(profile, ProfileExtensionState.Unknown);
                    else
                        _lastKnown[profile] = state;
                    if (state is ProfileExtensionState.Enabled or ProfileExtensionState.Disabled)
                        _hadExtension.Add(profile);

                    var off = state == ProfileExtensionState.Disabled ||
                              (state == ProfileExtensionState.Absent && _hadExtension.Contains(profile));
                    if (!off) continue;

                    // Only now look at the desktop: most scans find nothing off.
                    open ??= new HashSet<string>(
                        _windows.GetWindowAppIds(_browserProcessName), StringComparer.OrdinalIgnoreCase);
                    if (WindowAppIds(_userDataFolder, profile).Any(open.Contains))
                        found.Add(profile);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Reading Edge's profiles failed; treating them as having the extension.");
            }
            return found;
        }
    }

    /// <summary>
    /// The AppUserModelIDs Edge gives the browser windows of
    /// <paramref name="profile"/>: <c>MSEdge.</c> followed by the names of the user
    /// data folder and the profile folder, joined by a dot, with everything but
    /// ASCII letters, digits and dots dropped (<c>MSEdge.UserData.Profile1</c>).
    /// The Default profile of the default user data folder is plain <c>MSEdge</c>.
    /// An app window (an installed site) has an ID of its own that starts with the
    /// app's, so it never counts as the profile being open in the browser.
    /// </summary>
    public static IReadOnlyList<string> WindowAppIds(string userDataFolder, string profile)
    {
        var folder = Path.GetFileName(userDataFolder.TrimEnd('\\', '/'));
        var id = $"{BrowserAppId}.{Sanitize($"{folder}.{profile}")}";
        return profile.Equals("Default", StringComparison.OrdinalIgnoreCase) ? [BrowserAppId, id] : [id];
    }

    private static string Sanitize(string value) =>
        new(value.Where(c => char.IsAsciiLetterOrDigit(c) || c == '.').ToArray());

    /// <summary>
    /// What <paramref name="profileFolder"/>'s preferences say about Anchor's
    /// extension. Edge keeps the extension settings in <c>Secure Preferences</c> on
    /// Windows and in <c>Preferences</c> elsewhere and for some installs; either
    /// counts. With several of Anchor's IDs installed (a test-ring laptop), one of
    /// them on is enough.
    /// </summary>
    internal ProfileExtensionState ReadState(string profileFolder)
    {
        var result = ProfileExtensionState.Absent;
        foreach (var file in PreferenceFiles)
        {
            var state = ReadFile(Path.Combine(profileFolder, file));
            if (state == ProfileExtensionState.Enabled) return state;
            if (state == ProfileExtensionState.Disabled ||
                (state == ProfileExtensionState.Unknown && result == ProfileExtensionState.Absent))
                result = state;
        }
        return result;
    }

    private ProfileExtensionState ReadFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            _files.Remove(path);
            return ProfileExtensionState.Absent;
        }
        if (_files.TryGetValue(path, out var seen) &&
            seen.Written == info.LastWriteTimeUtc && seen.Length == info.Length)
            return seen.State;

        ProfileExtensionState state;
        try
        {
            // Edge replaces the file in one move, and may be doing so right now.
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            state = StateIn(doc.RootElement);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.LogDebug(ex, "Could not read {File}; keeping what it said before.", Path.GetFileName(path));
            return ProfileExtensionState.Unknown;
        }
        _files[path] = (info.LastWriteTimeUtc, info.Length, state);
        return state;
    }

    private ProfileExtensionState StateIn(JsonElement preferences)
    {
        if (preferences.ValueKind != JsonValueKind.Object ||
            !preferences.TryGetProperty("extensions", out var extensions) ||
            extensions.ValueKind != JsonValueKind.Object ||
            !extensions.TryGetProperty("settings", out var settings) ||
            settings.ValueKind != JsonValueKind.Object)
            return ProfileExtensionState.Absent;

        var result = ProfileExtensionState.Absent;
        foreach (var id in _extensionIds)
        {
            if (!settings.TryGetProperty(id, out var entry)) continue;
            var state = StateOf(entry);
            if (state == ProfileExtensionState.Enabled) return state;
            if (state == ProfileExtensionState.Disabled) result = state;
        }
        return result;
    }

    /// <summary>
    /// One <c>extensions.settings</c> entry. An installed extension has a
    /// <c>location</c>; Edge leaves an empty entry behind in some profiles, which
    /// is not an install. Switched off, it has a non-empty <c>disable_reasons</c>
    /// (a list today, a bit mask in older Edge versions, which also wrote
    /// <c>state</c>: 0 off, 1 on, 2 an uninstalled external extension).
    /// </summary>
    internal static ProfileExtensionState StateOf(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("location", out _))
            return ProfileExtensionState.Absent;

        if (entry.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Number &&
            state.TryGetInt32(out var stateValue))
        {
            if (stateValue == 2) return ProfileExtensionState.Absent;
            if (stateValue == 0) return ProfileExtensionState.Disabled;
        }

        if (entry.TryGetProperty("disable_reasons", out var reasons))
        {
            var disabled = reasons.ValueKind switch
            {
                JsonValueKind.Array => reasons.GetArrayLength() > 0,
                JsonValueKind.Number => reasons.TryGetInt64(out var mask) && mask != 0,
                _ => false,
            };
            if (disabled) return ProfileExtensionState.Disabled;
        }
        return ProfileExtensionState.Enabled;
    }
}
