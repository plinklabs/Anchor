using System.Text.Json.Serialization;

namespace Anchor.Domain.Users;

// Serialize as the enum name on the wire so dashboards can gate features by
// role with a stable string contract (#75 needed "Admin" to be the wire value
// for the catalogue editor link). Keep the attribute here rather than in a
// global JsonOptions config so contract-shape doesn't depend on host wiring.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UserRole
{
    Student,
    Teacher,
    Admin
}
