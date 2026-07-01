using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FocusAgent.Core.Tests;

/// <summary>
/// Locks the agent's string catalogues (#323, part of #320) in parity: every
/// locale must carry exactly the same keys as the <c>en-US</c> source, no value may
/// be blank, and each translation must reuse the same <c>{0}</c>/<c>{1}</c> format
/// placeholders as its English source. A missing key would render English at
/// runtime (harmless) but a <em>stray</em> or <em>renamed</em> key, an empty
/// string, or a dropped placeholder is a real defect this fails fast on — the
/// analog of the extension's en/nl messages.json parity lock.
///
/// <para>
/// This is a pure file check (no WinUI/MRT), so it lives with the Core unit tests;
/// the compiled-<c>resources.pri</c> + language-fallback behaviour is proven
/// separately by the real-exe <c>I18nTests</c> integration spec.
/// </para>
/// </summary>
public class I18nCatalogueParityTests
{
    private const string SourceLocale = "en-US";
    private static readonly string[] TranslationLocales = { "nl-NL" };

    [Fact]
    public void Every_locale_has_exactly_the_source_keys()
    {
        var source = LoadCatalogue(SourceLocale);
        Assert.NotEmpty(source);

        foreach (var locale in TranslationLocales)
        {
            var translated = LoadCatalogue(locale);

            var missing = source.Keys.Except(translated.Keys).OrderBy(k => k).ToList();
            var stray = translated.Keys.Except(source.Keys).OrderBy(k => k).ToList();

            Assert.True(missing.Count == 0, $"{locale} is missing keys: {string.Join(", ", missing)}");
            Assert.True(stray.Count == 0, $"{locale} has keys absent from {SourceLocale}: {string.Join(", ", stray)}");
        }
    }

    [Fact]
    public void No_value_is_blank_in_any_locale()
    {
        foreach (var locale in TranslationLocales.Prepend(SourceLocale))
        {
            foreach (var (key, value) in LoadCatalogue(locale))
                Assert.False(string.IsNullOrWhiteSpace(value), $"{locale} key '{key}' is blank.");
        }
    }

    [Fact]
    public void Translations_reuse_the_source_format_placeholders()
    {
        var source = LoadCatalogue(SourceLocale);

        foreach (var locale in TranslationLocales)
        {
            var translated = LoadCatalogue(locale);
            foreach (var (key, sourceValue) in source)
            {
                if (!translated.TryGetValue(key, out var translatedValue)) continue; // covered by the key-parity test
                Assert.Equal(Placeholders(sourceValue), Placeholders(translatedValue));
            }
        }
    }

    /// <summary>The set of <c>{N}</c> format placeholders used in a value.</summary>
    private static SortedSet<string> Placeholders(string value) =>
        new(Regex.Matches(value, @"\{\d+\}").Select(m => m.Value));

    private static Dictionary<string, string> LoadCatalogue(string locale)
    {
        var path = Path.Combine(StringsDir(), locale, "Resources.resw");
        Assert.True(File.Exists(path), $"Catalogue not found: {path}");

        var doc = XDocument.Load(path);
        return doc.Root!
            .Elements("data")
            .ToDictionary(
                d => (string)d.Attribute("name")!,
                d => d.Element("value")?.Value ?? "",
                StringComparer.Ordinal);
    }

    private static string StringsDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "agent", "src", "FocusAgent.App", "Strings");
            if (Directory.Exists(candidate)) return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException(
            "Could not locate agent/src/FocusAgent.App/Strings walking up from the test binary.");
    }
}
