using System.Text.Json;

namespace FocusAgent.Core.Extension;

/// <summary>
/// The extension IDs the agent takes for Anchor's extension (#497): the Edge
/// Add-ons store build's (<see cref="EdgeExtensionPolicy.ExtensionId"/>), always,
/// plus the self-hosted test-ring build's, once one is configured.
///
/// The test ring is a second Intune profile that force-installs a build of the
/// extension signed with our own key and served from our own update URL, so a
/// change reaches a few test laptops without waiting for the store's review. The
/// key fixes that build's ID, so it isn't the store's. A laptop of the test ring
/// has to count as having the extension (its forcelist entry and its install in
/// an Edge profile) and its extension has to be let onto the witness link, just
/// like the store build. What the agent writes itself stays the store's: a manual
/// install's forcelist entry and the guided install's store listing.
///
/// The test-ring ID is configured in one place, <c>extension/test-ring.json</c>,
/// which the build embeds (see <c>FocusAgent.Core.csproj</c>). It is empty until
/// the test ring's signing key exists (<c>extension/scripts/test-ring-key.mjs</c>
/// writes it, see <c>docs/RELEASE.md</c>), and empty, the agent takes only the
/// store ID, exactly as before.
/// </summary>
public sealed class AnchorExtensionIds
{
    /// <summary>The logical name of the embedded <c>extension/test-ring.json</c>.</summary>
    public const string TestRingResourceName = "FocusAgent.Core.Extension.test-ring.json";

    private static readonly Lazy<AnchorExtensionIds> ConfiguredIds = new(LoadConfigured);

    private AnchorExtensionIds(string? testRing)
    {
        TestRing = testRing;
        All = testRing is null || testRing == EdgeExtensionPolicy.ExtensionId
            ? [EdgeExtensionPolicy.ExtensionId]
            : [EdgeExtensionPolicy.ExtensionId, testRing];
    }

    /// <summary>The store build's ID only: what the agent took before #497.</summary>
    public static AnchorExtensionIds StoreOnly { get; } = new(null);

    /// <summary>
    /// The IDs from this build's <c>extension/test-ring.json</c>: the store's, plus
    /// the test ring's when the file names one. A file that's missing, unreadable or
    /// names something that isn't an extension ID counts as naming none, so the
    /// agent never takes less than the store build.
    /// </summary>
    public static AnchorExtensionIds Configured => ConfiguredIds.Value;

    /// <summary>
    /// The store's ID plus <paramref name="testRingId"/>. A blank or malformed
    /// <paramref name="testRingId"/> gives <see cref="StoreOnly"/>.
    /// </summary>
    public static AnchorExtensionIds WithTestRing(string? testRingId) =>
        Normalize(testRingId) is { } id ? new AnchorExtensionIds(id) : StoreOnly;

    /// <summary>The self-hosted test-ring build's ID, or null when none is configured.</summary>
    public string? TestRing { get; }

    /// <summary>Every ID that is Anchor's, the store's first.</summary>
    public IReadOnlyList<string> All { get; }

    /// <summary>
    /// True when <paramref name="forcelistValue"/> force-installs one of
    /// <see cref="All"/>: its id segment (before the first <c>;</c>) is one of them,
    /// whatever the update URL. The store entry and the test-ring entry
    /// (<c>&lt;test-ring-id&gt;;&lt;our update URL&gt;</c>) both count.
    /// </summary>
    public bool MatchesForcelistEntry(string? forcelistValue) =>
        All.Any(id => EdgeExtensionPolicy.IsEntryFor(forcelistValue, id));

    /// <summary>
    /// Whether <paramref name="id"/> has the shape of an extension ID Edge
    /// generates: 32 letters <c>a</c>–<c>p</c> (the first 16 bytes of the SHA-256 of
    /// the public key, each hex digit <c>0</c>–<c>f</c> written as <c>a</c>–<c>p</c>).
    /// </summary>
    public static bool IsExtensionId(string? id) =>
        id is { Length: 32 } && id.All(c => c is >= 'a' and <= 'p');

    /// <summary>
    /// The test-ring ID named in the JSON of <c>extension/test-ring.json</c>
    /// (<c>{"extensionId": "…"}</c>), or null when it names none or isn't valid.
    /// </summary>
    public static string? ReadTestRingId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("extensionId", out var id)
                   && id.ValueKind == JsonValueKind.String
                ? Normalize(id.GetString())
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Normalize(string? id)
    {
        var trimmed = id?.Trim().ToLowerInvariant();
        return IsExtensionId(trimmed) ? trimmed : null;
    }

    private static AnchorExtensionIds LoadConfigured()
    {
        using var stream = typeof(AnchorExtensionIds).Assembly.GetManifestResourceStream(TestRingResourceName);
        if (stream is null) return StoreOnly;
        using var reader = new StreamReader(stream);
        return WithTestRing(ReadTestRingId(reader.ReadToEnd()));
    }
}
