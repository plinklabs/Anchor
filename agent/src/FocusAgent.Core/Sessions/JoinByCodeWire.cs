using System.Net.Http.Json;
using System.Text.Json;

namespace FocusAgent.Core.Sessions;

/// <summary>
/// What the agent sends to and reads from <c>POST /sessions/join-by-code</c>
/// (#34): the body with the code the student typed, and the message the backend
/// sends with a refusal. Here rather than in the app's
/// <c>HttpJoinByCodeClient</c>, which turns each answer into the dialog's
/// localized text, so the unit tests can check both against
/// contracts/agent-rest.json (#573).
/// </summary>
public static class JoinByCodeWire
{
    /// <summary>The route, relative to the backend's base address.</summary>
    public const string Route = "sessions/join-by-code";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The request body: the code as the student typed it.</summary>
    public static HttpContent Body(string code) => JsonContent.Create(new JoinByCodeRequestBody(code), options: Json);

    /// <summary>
    /// The message the backend sends with a refusal (<c>{"message": "…"}</c>),
    /// or null when the body carries none.
    /// </summary>
    public static async Task<string?> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken ct = default)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
                return null;
            var parsed = JsonSerializer.Deserialize<JoinByCodeErrorBody>(body, Json);
            return string.IsNullOrWhiteSpace(parsed?.Message) ? null : parsed.Message;
        }
        catch
        {
            // A body that isn't the message (a proxy's error page, a cut-off
            // read): the dialog shows its own text instead.
            return null;
        }
    }

    private sealed record JoinByCodeRequestBody(string Code);

    private sealed record JoinByCodeErrorBody(string? Message);
}
