using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FocusAgent.Core.Tamper;

/// <summary>An installed site's app, as Edge registered it with Windows (#537).</summary>
/// <param name="AppId">
/// Edge's ID for the app (<c>--app-id</c>), under which Edge's <c>Local State</c>
/// lists the profiles it is installed in (<c>app_shims</c>).
/// </param>
/// <param name="UserDataFolder">The <c>User Data</c> folder it was installed from.</param>
/// <param name="Profile">
/// The profile it was first installed in (its folder, <c>Profile 1</c>); null when
/// the package doesn't say.
/// </param>
public sealed record EdgeWebApp(string AppId, string UserDataFolder, string? Profile);

/// <summary>Tells the installed site's app an app window belongs to (#537).</summary>
public interface IEdgeWebApps
{
    /// <summary>
    /// The app whose windows carry <paramref name="appUserModelId"/>, or null when
    /// it is no app of the stable Edge's (a browser window, another app's window, a
    /// package that isn't there). Best-effort: never throws.
    /// </summary>
    EdgeWebApp? Find(string appUserModelId);
}

/// <summary>
/// Reads the installed sites' apps from the packages Edge registers for them
/// (#537). Measured on Edge 157: installing a site as an app registers a package
/// of its own for the student (<c>localhost-AB457204_ebq0fnkrwaj2c</c>; Edge hosts
/// it), and the app's windows carry that package's AppUserModelID
/// (<c>localhost-AB457204_ebq0fnkrwaj2c!App</c>), whatever profile they belong to.
/// The package's <c>AppxManifest.xml</c> names the rest:
/// <list type="bullet">
/// <item>Edge as the host: a <c>uap10:HostRuntimeDependency</c> on
/// <c>Microsoft.MicrosoftEdge.Stable</c>, and <c>uap10:HostId="PWA"</c> on the
/// application.</item>
/// <item>Its <c>uap10:Parameters</c>: <c>--app-id=&lt;id&gt;</c>, and
/// <c>--user-data-dir="&lt;folder&gt;"</c> when the app comes from a <c>User Data</c>
/// folder other than Edge's own.</item>
/// <item>The profile it was installed in, in the description of its
/// <c>com.ms.webapp.internals.*</c> app extension:
/// <c>parameters?…;profile-directory?Default;start-url?…</c>. Installed in a
/// second profile, the app keeps the one package (and its windows the one ID),
/// which goes on naming the first.</item>
/// </list>
/// The package folder is read for each new ID once and again only when its
/// manifest changes. Reading only, without elevation: the packages are the
/// student's own.
/// </summary>
public sealed class EdgeWebAppPackages : IEdgeWebApps
{
    /// <summary>The package Edge's installed-site apps run on: the stable channel's.</summary>
    public const string EdgeHostPackage = "Microsoft.MicrosoftEdge.Stable";

    private static readonly XNamespace Uap10 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";
    private static readonly XNamespace Uap3 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/3";

    private static readonly Regex AppIdParameter = new(@"--app-id=(?<id>[^\s""]+)", RegexOptions.CultureInvariant);
    private static readonly Regex UserDataParameter =
        new(@"--user-data-dir=(?:""(?<dir>[^""]*)""|(?<dir>\S+))", RegexOptions.CultureInvariant);
    private static readonly Regex ProfileParameter =
        new(@"--profile-directory=(?:""(?<dir>[^""]*)""|(?<dir>\S+))", RegexOptions.CultureInvariant);

    private readonly Func<string, string?> _packageFolder;
    private readonly string _defaultUserDataFolder;
    private readonly ILogger<EdgeWebAppPackages> _log;
    private readonly object _gate = new();
    // Per AppUserModelID: the manifest as last read (its write time and size) and what it said.
    private readonly Dictionary<string, (string Manifest, DateTime Written, long Length, EdgeWebApp? App)> _read =
        new(StringComparer.OrdinalIgnoreCase);

    /// <param name="packageFolder">
    /// The folder of the package of a package family registered for the student, or
    /// null when there is none.
    /// </param>
    /// <param name="defaultUserDataFolder">
    /// Edge's own <c>User Data</c> folder, which an app without
    /// <c>--user-data-dir</c> comes from (<see cref="EdgeDefaultUserDataFolder"/>
    /// by default).
    /// </param>
    public EdgeWebAppPackages(
        Func<string, string?> packageFolder,
        string? defaultUserDataFolder = null,
        ILogger<EdgeWebAppPackages>? log = null)
    {
        _packageFolder = packageFolder;
        _defaultUserDataFolder = defaultUserDataFolder ?? EdgeDefaultUserDataFolder;
        _log = log ?? NullLogger<EdgeWebAppPackages>.Instance;
    }

    /// <summary>Edge's own <c>User Data</c> folder, under <c>%LOCALAPPDATA%</c>.</summary>
    public static string EdgeDefaultUserDataFolder =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Edge", "User Data");

    public EdgeWebApp? Find(string appUserModelId)
    {
        // A packaged app's ID is its package family (name_publisher) and the
        // application in it: localhost-AB457204_ebq0fnkrwaj2c!App.
        var bang = appUserModelId.LastIndexOf('!');
        if (bang <= 0 || bang == appUserModelId.Length - 1) return null;
        var family = appUserModelId[..bang];
        if (!family.Contains('_') || family.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;

        lock (_gate)
        {
            try
            {
                if (_read.TryGetValue(appUserModelId, out var seen))
                {
                    var known = new FileInfo(seen.Manifest);
                    if (known.Exists && known.LastWriteTimeUtc == seen.Written && known.Length == seen.Length)
                        return seen.App;
                }

                var folder = _packageFolder(family);
                if (folder is null)
                {
                    _read.Remove(appUserModelId);
                    return null;
                }
                var manifest = new FileInfo(Path.Combine(folder, "AppxManifest.xml"));
                if (!manifest.Exists)
                {
                    _read.Remove(appUserModelId);
                    return null;
                }
                using var stream = new FileStream(
                    manifest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var app = Parse(stream, appUserModelId[(bang + 1)..], _defaultUserDataFolder);
                _read[appUserModelId] = (manifest.FullName, manifest.LastWriteTimeUtc, manifest.Length, app);
                if (app is not null)
                    _log.LogInformation(
                        "Window ID {AppUserModelId} is Edge app {AppId} (first installed in {Profile})",
                        appUserModelId, app.AppId, app.Profile ?? "an unnamed profile");
                return app;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
            {
                _log.LogDebug(ex, "Could not read the package of {AppUserModelId}", appUserModelId);
                return null;
            }
        }
    }

    /// <summary>
    /// What a package's <c>AppxManifest.xml</c> says of the installed site's app
    /// <paramref name="applicationId"/> (<c>App</c>), or null when it is no app of
    /// the stable Edge's.
    /// </summary>
    internal static EdgeWebApp? Parse(Stream manifest, string applicationId, string defaultUserDataFolder)
    {
        using var reader = XmlReader.Create(
            manifest, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var package = XDocument.Load(reader).Root;
        if (package is null) return null;
        var ns = package.Name.Namespace;

        var hostedByEdge = package.Element(ns + "Dependencies")?.Elements(Uap10 + "HostRuntimeDependency")
            .Any(d => string.Equals((string?)d.Attribute("Name"), EdgeHostPackage, StringComparison.OrdinalIgnoreCase)) == true;
        if (!hostedByEdge) return null;

        var application = package.Element(ns + "Applications")?.Elements(ns + "Application")
            .FirstOrDefault(a => string.Equals((string?)a.Attribute("Id"), applicationId, StringComparison.OrdinalIgnoreCase));
        if (application is null
            || !string.Equals((string?)application.Attribute(Uap10 + "HostId"), "PWA", StringComparison.OrdinalIgnoreCase))
            return null;

        var parameters = (string?)application.Attribute(Uap10 + "Parameters") ?? "";
        var appId = AppIdParameter.Match(parameters);
        if (!appId.Success) return null;
        var userData = UserDataParameter.Match(parameters);

        return new EdgeWebApp(
            appId.Groups["id"].Value,
            userData.Success && userData.Groups["dir"].Value.Trim() is { Length: > 0 } dir ? dir : defaultUserDataFolder,
            ProfileOf(application, parameters));
    }

    private static string? ProfileOf(XElement application, string parameters)
    {
        var given = ProfileParameter.Match(parameters);
        if (given.Success && given.Groups["dir"].Value.Trim() is { Length: > 0 } fromParameters)
            return fromParameters;

        // parameters?…;profile-directory?Default;start-url?…
        foreach (var extension in application.Descendants(Uap3 + "AppExtension"))
        {
            if ((string?)extension.Attribute("Name") is not { } name
                || !name.StartsWith("com.ms.webapp.internals", StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var part in ((string?)extension.Attribute("Description") ?? "").Split(';'))
            {
                const string key = "profile-directory?";
                if (part.StartsWith(key, StringComparison.OrdinalIgnoreCase) && part[key.Length..].Trim() is { Length: > 0 } profile)
                    return profile;
            }
        }
        return null;
    }
}
