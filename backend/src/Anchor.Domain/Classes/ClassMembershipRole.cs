using System.Text.Json.Serialization;

namespace Anchor.Domain.Classes;

// Serialized by name on the wire, like the other wire enums (UserRole,
// EventKind, BundleEntryKind, BundleEntryMatchType): the dashboard sends
// "role":"Member" when it adds or imports a student, which the default numeric
// form refused with a 400 (#393). Numbers are still read.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClassMembershipRole
{
    Member,
    Teacher
}
