using System.Text.Json.Serialization;

namespace Anchor.Domain.Bundles;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BundleEntryKind
{
    Domain,
    App
}
