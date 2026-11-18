using System.Text.Json;
using FocusAgent.Core.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusAgent.Core.Tamper;

/// <summary>A browser window as the school-profile rule sees it (#525).</summary>
/// <param name="Handle">The top-level window.</param>
/// <param name="AppId">
/// Its explicit AppUserModelID: Edge gives every browser window that of its
/// profile (<see cref="EdgeOpenProfileExtensionProbe.WindowAppIds"/>). Null when it
/// has none.
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
    /// Not an Edge browser window the rule can place: no AppUserModelID yet, an
    /// installed site's app window, or no school profile is known. Never paused.
    /// </summary>
    Unknown,

    /// <summary>An ordinary window of the school profile: the one place Edge may be used during a session.</summary>
    SchoolProfile,

    /// <summary>An InPrivate window, of the school profile or of another one.</summary>
    InPrivate,

    /// <summary>A window of another profile.</summary>
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
/// </list>
/// Reading only, without elevation: the files and windows are the student's own.
/// </summary>
public sealed class EdgeSchoolProfileProbe
{
    /// <summary>The folder Edge keeps the Guest profile in.</summary>
    public const string GuestProfile = "Guest Profile";

    private readonly string _userDataFolder;
    private readonly IOpenProfileExtensionProbe _extension;
    private readonly ILogger<EdgeSchoolProfileProbe> _log;
    private readonly string _browserWindowPrefix;
    private readonly object _gate = new();

    // Local State as last read: its write time and size, and the profiles in it,
    // so an unchanged file is not parsed again and a moment it can't be read
    // keeps what it said.
    private (DateTime Written, long Length, IReadOnlyList<ProfileAccount> Profiles)? _localState;

    private sealed record ProfileAccount(string Profile, string? UserName, string? ObjectId);

    /// <param name="userDataFolder">Edge's <c>User Data</c> folder.</param>
    /// <param name="extension">Reads whether a profile has Anchor's extension.</param>
    public EdgeSchoolProfileProbe(
        string userDataFolder,
        IOpenProfileExtensionProbe extension,
        ILogger<EdgeSchoolProfileProbe>? log = null)
    {
        _userDataFolder = userDataFolder.TrimEnd('\\', '/');
        _extension = extension;
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

        var profiles = ReadProfiles();
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
    /// Which profile a browser window belongs to, by its AppUserModelID
    /// <paramref name="appId"/> and its <paramref name="title"/>, with
    /// <paramref name="schoolProfile"/> the school profile's folder (null when
    /// there is none: then every browser window is outside it).
    /// </summary>
    public EdgeWindowProfile Classify(string? schoolProfile, string? appId, string? title)
    {
        if (string.IsNullOrEmpty(appId))
            return EdgeWindowProfile.Unknown;

        if (schoolProfile is not null && IsWindowOf(schoolProfile, appId))
            return InPrivateDetection.HasInPrivateTitle(title) ? EdgeWindowProfile.InPrivate : EdgeWindowProfile.SchoolProfile;
        if (IsWindowOf(GuestProfile, appId))
            return EdgeWindowProfile.Guest;
        if (!IsBrowserWindowAppId(appId))
            return EdgeWindowProfile.Unknown;
        return InPrivateDetection.HasInPrivateTitle(title) ? EdgeWindowProfile.InPrivate : EdgeWindowProfile.OtherProfile;
    }

    /// <summary>True for the profiles' windows the school-profile rule pauses during a session.</summary>
    public static bool IsOutside(EdgeWindowProfile profile) =>
        profile is EdgeWindowProfile.OtherProfile or EdgeWindowProfile.Guest or EdgeWindowProfile.InPrivate;

    private bool IsWindowOf(string profile, string appId) =>
        EdgeOpenProfileExtensionProbe.WindowAppIds(_userDataFolder, profile)
            .Contains(appId, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The AppUserModelID of a browser window of some profile of this user data
    /// folder: <c>MSEdge</c>, or <c>MSEdge.UserData.</c> and one profile folder. An
    /// installed site's app window has an ID of its own and is left alone.
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
    /// The profiles in Edge's <c>Local State</c> and the account each is signed in
    /// with, or null when there is no such file (or it never could be read).
    /// </summary>
    private IReadOnlyList<ProfileAccount>? ReadProfiles()
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
                    return seen.Profiles;

                // Edge replaces the file in one move, and may be doing so right now.
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var doc = JsonDocument.Parse(stream);
                var profiles = ProfilesIn(doc.RootElement);
                if (profiles is null)
                    return null;
                _localState = (info.LastWriteTimeUtc, info.Length, profiles);
                return profiles;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _log.LogDebug(ex, "Could not read Edge's Local State; keeping what it said before.");
                return _localState?.Profiles;
            }
        }
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
