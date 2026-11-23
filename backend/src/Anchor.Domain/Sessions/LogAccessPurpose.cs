using System.Text.Json.Serialization;

namespace Anchor.Domain.Sessions;

/// <summary>
/// Why an admin downloaded a student's log upload (#566): the two uses the
/// privacy policy (#565) allows, and nothing else.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LogAccessPurpose
{
    /// <summary>Finding out what went wrong with the agent, the extension or the session.</summary>
    TechnicalDiagnosis,

    /// <summary>
    /// A concrete suspicion of fraud during a test or exam, only at the request
    /// of the school's leadership, who is named in
    /// <see cref="LogAccess.RequestedBy"/>.
    /// </summary>
    FraudSuspicion,
}
