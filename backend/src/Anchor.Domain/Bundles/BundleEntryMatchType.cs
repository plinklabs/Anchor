using System.Text.Json.Serialization;

namespace Anchor.Domain.Bundles;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BundleEntryMatchType
{
    Exact,
    Wildcard,
    Suffix,
    SignedPublisher
}
