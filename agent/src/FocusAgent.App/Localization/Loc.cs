using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace FocusAgent.App.Localization;

/// <summary>
/// The single seam every agent surface uses to resolve localized strings (i18n,
/// #323 / part of #320). Both static window copy (set in each window's constructor
/// from its <c>x:Name</c>'d elements) and the strings assembled at runtime (tray
/// labels, connection status/detail, toast + heartbeat text, join errors) go
/// through here — the only place that touches
/// <see cref="ResourceLoader"/>/<see cref="ResourceManager"/>.
///
/// <para>
/// <b>Why not <c>x:Uid</c>?</b> The agent ships <em>unpackaged</em> (via Velopack),
/// and there <see cref="Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride"/>
/// throws — so the XAML framework's own <c>x:Uid</c> resolution can only ever follow
/// the machine's language list and can't be pointed at a chosen language. Applying
/// strings in code against an <em>explicit</em> MRT <see cref="ResourceContext"/>
/// gives one uniform path that works both by OS language (production) and forced
/// (the <c>--ui-language</c> PoC/test flag).
/// </para>
///
/// <para>
/// <b>Locale resolution.</b> With no override, MRT picks the catalogue from the
/// user's Windows display language and falls back to the <c>en-US</c> default (see
/// <c>&lt;DefaultLanguage&gt;</c> in the csproj) for an unsupported language or a
/// missing key — never a blank or a raw key. <see cref="SetStartupLanguage"/>
/// forces a specific language instead.
/// </para>
///
/// <para>
/// <b>Bootstrap order.</b> <see cref="SetStartupLanguage"/> is called from
/// <c>Program.Main</c> <em>before</em> the Windows App SDK runtime is bootstrapped,
/// so it touches only .NET APIs. The MRT <see cref="ResourceManager"/> (which needs
/// the bootstrap) is built lazily on the first <see cref="Get"/> — which only
/// happens once the app, and the runtime, are up.
/// </para>
/// </summary>
internal static class Loc
{
    private static string? _override;
    private static ResourceManager? _manager;
    private static ResourceMap? _map;
    private static ResourceContext? _context;

    /// <summary>
    /// Force the UI language for this process (the <c>--ui-language</c> dev flag /
    /// the <c>--verify-i18n</c> mode). Safe to call before the WinAppSDK runtime is
    /// up: it only pins the thread cultures (which steer
    /// <see cref="string.Format(IFormatProvider,string,object?[])"/> and framework
    /// copy) and records the tag; the MRT lookups in <see cref="Get"/> pin the
    /// language via a resource context built lazily from it.
    /// </summary>
    public static void SetStartupLanguage(string? bcp47)
    {
        if (string.IsNullOrWhiteSpace(bcp47)) return;
        _override = bcp47;

        try
        {
            var culture = CultureInfo.GetCultureInfo(bcp47);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch { /* an unknown BCP-47 tag falls through to the default culture. */ }
    }

    /// <summary>Resolve a flat resource key, falling back to the key itself if it's absent.</summary>
    public static string Get(string key)
    {
        try
        {
            var candidate = Context is { } ctx ? Map.TryGetValue(key, ctx) : Map.TryGetValue(key);
            var value = candidate?.ValueAsString;
            if (!string.IsNullOrEmpty(value)) return value!;
        }
        catch
        {
            // A resources.pri that failed to load must never crash a UI surface —
            // fall through to the key, which is at least diagnosable on screen.
        }
        return key;
    }

    /// <summary>Resolve <paramref name="key"/> and format it with <paramref name="args"/> under the current culture.</summary>
    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    private static ResourceManager Manager => _manager ??= new ResourceManager();

    private static ResourceMap Map => _map ??= Manager.MainResourceMap.GetSubtree("Resources");

    private static ResourceContext? Context
    {
        get
        {
            if (_override is null) return null;
            if (_context is not null) return _context;
            var ctx = Manager.CreateResourceContext();
            ctx.QualifierValues["Language"] = _override;
            return _context = ctx;
        }
    }
}
