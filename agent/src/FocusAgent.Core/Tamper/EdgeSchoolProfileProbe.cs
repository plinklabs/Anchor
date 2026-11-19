using System.Text.Json;
using FocusAgent.Core.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusAgent.Core.Tamper;

/// <summary>A browser window as the school-profile rule sees it (#525).</summary>
/// <param name="Handle">The top-level window.</param>
/// <param name="AppId">
/// Its explicit AppUserModelID: Edge gives every browser window that of its
/// profile (<see cref="EdgeOpenProfileExtensionProbe.WindowAppIds"/>), and an
/// installed site's app window that of the app's package
/// (<see cref="EdgeWebAppPackages"/>, #537). Null when it has none.
/// </param>
/// <param name="Title">Its title, which is all that tells an InPrivate window from its profile's others.</param>
/// <param name="Minimized">Whether it is minimized: a paused window that is down already needs nothing.</param>
public sealed record BrowserWindowInfo(nint Handle, string? AppId, string? Title, bool Minimized);

/// <summary>Reads the browser's windows for the school-profile rule (#525).</summary>
public interface IBrowserWindowReader
{
    /// <summary>
    /// The visible top-level windows of <paramref name="processName"/>, minimized
    /// ones included. Best-effort: a window that can't be read is left out.
    /// </summary>
    IReadOnlyList<BrowserWindowInfo> GetBrowserWindows(string processName);

    /// <summary>One window, read now; null when it is gone or can't be read.</summary>
    BrowserWindowInfo? Describe(nint windowHandle);
}

/// <summary>
/// What takes a student from a paused Edge window to their school profile (#525):
/// the paused window closes, and Edge opens in the school profile when no window
/// of it is open to bring forward.
/// </summary>
public interface IBrowserProfileLauncher
{
    /// <summary>Asks the window to close, as its close button does.</summary>
    void CloseWindow(nint windowHandle);

    /// <summary>
    /// Opens the browser in <paramref name="profile"/> (a profile folder,
    /// <c>Profile 1</c>), at <paramref name="url"/> when one is given: a tab of that
    /// profile, in a new window when none of it is open. The store listing opened
    /// this way adds the extension to that profile (#536).
    /// </summary>
    void OpenProfile(string profile, string? url = null);
}

/// <summary>Which Edge profile a browser window belongs to, as the school-profile rule judges it (#525).</summary>
public enum EdgeWindowProfile
{
    /// <summary>
    /// Not an Edge window the rule can place: no AppUserModelID yet, another app's
    /// window, an installed site's app window whose profile can't be told (#537), or
    /// no school profile is known. Never paused.
    /// </summary>
    Unknown,

    /// <summary>An ordinary window of the school profile: the one place Edge may be used during a session.</summary>
    SchoolProfile,

    /// <summary>
    /// The window of a site installed as an app in the school profile (Teams or
    /// Outlook installed as an app, #537): usable like the school profile's
    /// windows, but no browser window. It has no address bar to read, and the
    /// student isn't brought to it.
    /// </summary>
    SchoolProfileApp,

    /// <summary>An InPrivate window, of the school profile or of another one.</summary>
    InPrivate,

    /// <summary>A window of another profile: a browser window, or the window of a site installed as an app there (#537).</summary>
    OtherProfile,

    /// <summary>A Guest window.</summary>
    Guest,
}

/// <summary>Whether the student's school profile is there (#525).</summary>
public enum SchoolProfileStatus
{
    /// <summary>
    /// Can't be told: no session, the agent isn't signed in, or Edge's
    /// <c>Local State</c> can't be read (Edge never ran on this account). The rule
    /// is off.
    /// </summary>
    Unknown,

    /// <summary>A profile is signed in with the school account and has the extension.</summary>
    Found,

    /// <summary>
    /// No profile is signed in with the school account, or the one that is has
    /// never had the extension: Edge can't be used until there is one. The
    /// reading's <see cref="SchoolProfileReading.Profile"/> tells the two apart
    /// (#536): the student has to sign in, or to add Anchor to that profile.
    /// </summary>
    Missing,
}

/// <param name="Status">Whether the school profile is there.</param>
/// <param name="Profile">
/// The profile signed in with the school account (its folder, <c>Profile 1</c>),
/// also while it is <see cref="SchoolProfileStatus.Missing"/> for lack of the
/// extension; null when no profile is signed in with it.
/// </param>
public sealed record SchoolProfileReading(SchoolProfileStatus Status, string? Profile)
{
    public static readonly SchoolProfileReading Unknown = new(SchoolProfileStatus.Unknown, null);
}

/// <summary>
/// Finds the Edge profile signed in with the student's school account, and tells
/// which profile a browser window belongs to (#525). During a session only that
/// profile may be used: serving every profile right kept leaving gaps (#517, #518,
/// #519), and Guest and InPrivate windows are further ways around the extension.
/// <list type="bullet">
/// <item>Which account each profile is signed in with: Edge's <c>Local State</c>
/// keeps it per profile folder under <c>profile.info_cache</c>, as
/// <c>user_name</c> (the address) and, for a work or school account,
/// <c>edge_account_oid</c> (its Entra object ID). The school profile is the one
/// whose object ID is the agent's own account's; a profile without one is matched
/// on the address, ignoring case, so an alias that differs in case still counts.</item>
/// <item>It must also have Anchor's extension (its preferences, read through
/// <see cref="IOpenProfileExtensionProbe"/>): a profile that never had it browses
/// unfiltered (#519), so until it has it the school profile counts as
/// <see cref="SchoolProfileStatus.Missing"/>.</item>
/// <item>Which profile a window belongs to: its AppUserModelID, which Edge sets per
/// profile (<see cref="EdgeOpenProfileExtensionProbe.WindowAppIds"/>). Guest
/// windows carry the <c>Guest Profile</c> folder's; an InPrivate window carries
/// its own profile's and says <c>[InPrivate]</c> in its title (measured on Edge
/// 157).</item>
/// <item>Which profile an installed site's app window belongs to (#537): its
/// AppUserModelID is the app's package's, the same in every profile
/// (<see cref="IEdgeWebApps"/>), so the profiles come from <c>Local State</c>,
/// which lists those the app is installed in under
/// <c>app_shims.&lt;app id&gt;.installed_profiles</c> (the package names only the
/// first). An app installed in more than one profile has Edge end each of its
/// windows' titles with its profile's name, as Edge shows it in that profile's
/// browser windows (<c>Outlook - School</c>; measured on Edge 157), which is how
/// the school profile's own copy is told from another's.</item>
/// </list>
/// Reading only, without elevation: the files and windows are the student's own.
/// </summary>
public sealed class EdgeSchoolProfileProbe
{
    /// <summary>The folder Edge keeps the Guest profile in.</summary>
    public const string GuestProfile = "Guest Profile";

    private readonly string _userDataFolder;
    private readonly IOpenProfileExtensionProbe _extension;
    private readonly IEdgeWebApps? _apps;
    private readonly ILogger<EdgeSchoolProfileProbe> _log;
    private readonly string _browserWindowPrefix;
    private readonly object _gate = new();

    // Local State as last read: its write time and size, and what it said, so an
    // unchanged file is not parsed again and a moment it can't be read keeps what
    // it said.
    private (DateTime Written, long Length, LocalState State)? _localState;

    private sealed record ProfileAccount(string Profile, string? UserName, string? ObjectId);

    /// <param name="Profiles">The profiles and the account each is signed in with.</param>
    /// <param name="AppProfiles">Per installed site's app (its ID), the profiles it is installed in.</param>
    private sealed record LocalState(
        IReadOnlyList<ProfileAccount> Profiles, IReadOnlyDictionary<string, IReadOnlyList<string>> AppProfiles);

    /// <param name="userDataFolder">Edge's <c>User Data</c> folder.</param>
    /// <param name="extension">Reads whether a profile has Anchor's extension.</param>
    /// <param name="apps">
    /// Tells the installed site's app an app window belongs to (#537); without it,
    /// app windows are <see cref="EdgeWindowProfile.Unknown"/>.
    /// </param>
    public EdgeSchoolProfileProbe(
        string userDataFolder,
        IOpenProfileExtensionProbe extension,
        ILogger<EdgeSchoolProfileProbe>? log = null,
        IEdgeWebApps? apps = null)
    {
        _userDataFolder = userDataFolder.TrimEnd('\\', '/');
        _extension = extension;
        _apps = apps;
        _log = log ?? NullLogger<EdgeSchoolProfileProbe>.Instance;
        var folder = EdgeOpenProfileExtensionProbe.Sanitize(Path.GetFileName(_userDataFolder));
        _browserWindowPrefix = $"{EdgeOpenProfileExtensionProbe.BrowserAppId}.{folder}.";
    }

    /// <summary>
    /// Where the school profile of <paramref name="account"/> stands. Never
    /// throws: what can't be read is <see cref="SchoolProfileStatus.Unknown"/>.
    /// </summary>
    public SchoolProfileReading Find(SchoolAccount? account)
    {
        if (account is null || (string.IsNullOrWhiteSpace(account.Username) && string.IsNullOrWhiteSpace(account.ObjectId)))
            return SchoolProfileReading.Unknown;

        var profiles = ReadLocalState()?.Profiles;
        if (profiles is null)
            return SchoolProfileReading.Unknown;

        var school = profiles.FirstOrDefault(p => IsSignedInWith(p, account));
        if (school is null)
            return new SchoolProfileReading(SchoolProfileStatus.Missing, null);

        // A profile that never had the extension browses unfiltered (#519). One
        // whose preferences can't be read is given the benefit of the doubt, and
        // one with the extension switched off is the extension guard's (#518).
        var state = _extension.ExtensionState(school.Profile);
        return new SchoolProfileReading(
            state == ProfileExtensionState.Absent ? SchoolProfileStatus.Missing : SchoolProfileStatus.Found,
            school.Profile);
    }

    /// <summary>
    /// Which profile an Edge window belongs to, by its AppUserModelID
    /// <paramref name="appId"/> and its <paramref name="title"/>, with
    /// <paramref name="schoolProfile"/> the school profile's folder (null when
    /// there is none: then every browser and app window is outside it), and
    /// <paramref name="schoolProfileName"/> the name Edge shows for it
    /// (<see cref="SchoolProfileName"/>; null while not known), which places the
    /// window of an app installed in the school profile and another one too (#537).
    /// </summary>
    public EdgeWindowProfile Classify(
        string? schoolProfile, string? appId, string? title, string? schoolProfileName = null)
    {
        if (string.IsNullOrEmpty(appId))
            return EdgeWindowProfile.Unknown;

        if (schoolProfile is not null && IsWindowOf(schoolProfile, appId))
            return InPrivateDetection.HasInPrivateTitle(title) ? EdgeWindowProfile.InPrivate : EdgeWindowProfile.SchoolProfile;
        if (IsWindowOf(GuestProfile, appId))
            return EdgeWindowProfile.Guest;
        if (IsBrowserWindowAppId(appId))
            return InPrivateDetection.HasInPrivateTitle(title) ? EdgeWindowProfile.InPrivate : EdgeWindowProfile.OtherProfile;
        return _apps?.Find(appId) is { } app
            ? ClassifyApp(schoolProfile, app, title, schoolProfileName)
            : EdgeWindowProfile.Unknown;
    }

    /// <summary>True for the profiles' windows the school-profile rule pauses during a session.</summary>
    public static bool IsOutside(EdgeWindowProfile profile) =>
        profile is EdgeWindowProfile.OtherProfile or EdgeWindowProfile.Guest or EdgeWindowProfile.InPrivate;

    /// <summary>
    /// The name Edge shows for <paramref name="schoolProfile"/>, read from the title
    /// of one of its ordinary browser windows among <paramref name="windows"/>
    /// (<c>Lesson - School - Microsoft Edge</c>: <c>School</c>); null when none is
    /// open. Edge ends the windows of an app installed in more than one profile
    /// with the same name (#537).
    /// </summary>
    public string? SchoolProfileName(string? schoolProfile, IEnumerable<BrowserWindowInfo> windows)
    {
        if (schoolProfile is null) return null;
        foreach (var window in windows)
        {
            if (window.AppId is not { Length: > 0 } appId || !IsWindowOf(schoolProfile, appId)
                || InPrivateDetection.HasInPrivateTitle(window.Title))
                continue;
            if (ShownProfileName(window.Title) is { } name)
                return name;
        }
        return null;
    }

    /// <summary>
    /// The profile's name in a browser window's title,
    /// <c>&lt;page&gt; - &lt;profile&gt; - Microsoft Edge</c> (measured on Edge 157);
    /// null for a title that doesn't have one.
    /// </summary>
    internal static string? ShownProfileName(string? title)
    {
        if (title is null) return null;
        var plain = title.Replace("​", "");
        const string end = " - Microsoft Edge";
        if (!plain.EndsWith(end, StringComparison.Ordinal)) return null;
        var rest = plain[..^end.Length];
        var dash = rest.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash < 0) return null;
        var name = rest[(dash + 3)..].Trim();
        return name.Length > 0 ? name : null;
    }

    /// <summary>
    /// The profile of the window of <paramref name="app"/>, a site installed as an
    /// app (#537): the one the app is installed in when that is one; the one its
    /// window's title names when it is several.
    /// </summary>
    private EdgeWindowProfile ClassifyApp(string? schoolProfile, EdgeWebApp app, string? title, string? schoolProfileName)
    {
        // An app of another User Data folder is no app of the school profile's.
        if (!SameFolder(app.UserDataFolder, _userDataFolder))
            return EdgeWindowProfile.OtherProfile;

        var localState = ReadLocalState();
        IReadOnlyList<string> installed = Array.Empty<string>();
        if (localState is not null && localState.AppProfiles.TryGetValue(app.AppId, out var listed))
        {
            // A profile deleted since is no longer in Edge's list of profiles.
            installed = listed
                .Where(p => localState.Profiles.Any(known => known.Profile.Equals(p, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        if (installed.Count == 0 && app.Profile is not null)
            installed = [app.Profile];
        if (installed.Count == 0)
            return EdgeWindowProfile.Unknown;

        if (schoolProfile is null || !installed.Contains(schoolProfile, StringComparer.OrdinalIgnoreCase))
            return EdgeWindowProfile.OtherProfile;
        if (installed.Count == 1)
            return EdgeWindowProfile.SchoolProfileApp;

        // Installed in the school profile and another: Edge ends each window's title
        // with its profile's name. Until the school profile's is known, the window
        // can't be placed.
        if (schoolProfileName is null)
            return EdgeWindowProfile.Unknown;
        return title?.Replace("​", "").EndsWith(" - " + schoolProfileName, StringComparison.OrdinalIgnoreCase) == true
            ? EdgeWindowProfile.SchoolProfileApp
            : EdgeWindowProfile.OtherProfile;
    }

    private static bool SameFolder(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private bool IsWindowOf(string profile, string appId) =>
        EdgeOpenProfileExtensionProbe.WindowAppIds(_userDataFolder, profile)
            .Contains(appId, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The AppUserModelID of a browser window of some profile of this user data
    /// folder: <c>MSEdge</c>, or <c>MSEdge.UserData.</c> and one profile folder. An
    /// installed site's app window carries its package's instead (#537).
    /// </summary>
    private bool IsBrowserWindowAppId(string appId)
    {
        if (appId.Equals(EdgeOpenProfileExtensionProbe.BrowserAppId, StringComparison.OrdinalIgnoreCase))
            return true;
        return appId.StartsWith(_browserWindowPrefix, StringComparison.OrdinalIgnoreCase)
               && appId.Length > _browserWindowPrefix.Length
               && !appId.AsSpan(_browserWindowPrefix.Length).Contains('.');
    }

    private static bool IsSignedInWith(ProfileAccount profile, SchoolAccount account)
    {
        // The stable account ID where both have one: an address can be an alias.
        if (!string.IsNullOrWhiteSpace(account.ObjectId) && !string.IsNullOrWhiteSpace(profile.ObjectId))
            return Guid.TryParse(account.ObjectId, out var mine) && Guid.TryParse(profile.ObjectId, out var theirs)
                ? mine == theirs
                : string.Equals(account.ObjectId.Trim(), profile.ObjectId.Trim(), StringComparison.OrdinalIgnoreCase);
        return !string.IsNullOrWhiteSpace(profile.UserName) && !string.IsNullOrWhiteSpace(account.Username)
               && string.Equals(profile.UserName.Trim(), account.Username.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What Edge's <c>Local State</c> says of the profiles and the installed sites'
    /// apps, or null when there is no such file (or it never could be read).
    /// </summary>
    private LocalState? ReadLocalState()
    {
        lock (_gate)
        {
            var path = Path.Combine(_userDataFolder, "Local State");
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    _localState = null;
                    return null;
                }
                if (_localState is { } seen && seen.Written == info.LastWriteTimeUtc && seen.Length == info.Length)
                    return seen.State;

                // Edge replaces the file in one move, and may be doing so right now.
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var doc = JsonDocument.Parse(stream);
                var profiles = ProfilesIn(doc.RootElement);
                if (profiles is null)
                    return null;
                var state = new LocalState(profiles, AppProfilesIn(doc.RootElement));
                _localState = (info.LastWriteTimeUtc, info.Length, state);
                return state;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _log.LogDebug(ex, "Could not read Edge's Local State; keeping what it said before.");
                return _localState?.State;
            }
        }
    }

    /// <summary>
    /// <c>app_shims.&lt;app id&gt;.installed_profiles</c>: the profiles each installed
    /// site's app is installed in (measured on Edge 157).
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> AppProfilesIn(JsonElement localState)
    {
        var apps = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (!localState.TryGetProperty("app_shims", out var shims) || shims.ValueKind != JsonValueKind.Object)
            return apps;
        foreach (var app in shims.EnumerateObject())
        {
            if (app.Value.ValueKind != JsonValueKind.Object
                || !app.Value.TryGetProperty("installed_profiles", out var installed)
                || installed.ValueKind != JsonValueKind.Array)
                continue;
            apps[app.Name] = installed.EnumerateArray()
                .Where(p => p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString()))
                .Select(p => p.GetString()!)
                .ToList();
        }
        return apps;
    }

    private static IReadOnlyList<ProfileAccount>? ProfilesIn(JsonElement localState)
    {
        if (localState.ValueKind != JsonValueKind.Object
            || !localState.TryGetProperty("profile", out var profile) || profile.ValueKind != JsonValueKind.Object
            || !profile.TryGetProperty("info_cache", out var cache) || cache.ValueKind != JsonValueKind.Object)
            return null;

        var profiles = new List<ProfileAccount>();
        foreach (var entry in cache.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            profiles.Add(new ProfileAccount(
                entry.Name,
                StringOf(entry.Value, "user_name"),
                StringOf(entry.Value, "edge_account_oid")));
        }
        return profiles;
    }

    private static string? StringOf(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
