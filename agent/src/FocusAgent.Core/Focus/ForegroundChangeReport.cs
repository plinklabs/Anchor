using System.Text.Json;

namespace FocusAgent.Core.Focus;

/// <summary>
/// The <c>ForegroundChange</c> event the agent reports for every foreground
/// switch during a session. The payload names the app and says whether it was
/// blocked — <c>{"processName":"…","publisher":"…","blocked":true}</c> — and
/// nothing more (#345). It used to carry the window title and the executable
/// path too: titles hold document names, chat partners and search terms, and
/// paths under the user profile hold the student's Windows user name. The
/// backend never parsed either; they were only shown as raw JSON on the
/// past-session page, so they were kept for weeks without being used.
/// </summary>
public static class ForegroundChangeReport
{
    /// <summary>
    /// Must parse (case-insensitive) to <c>Anchor.Domain.Events.EventKind.ForegroundChange</c>
    /// on the backend's <c>ReportEvent</c>. The agent doesn't reference the backend enum.
    /// </summary>
    public const string Kind = "ForegroundChange";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The JSON payload reported for <paramref name="change"/>.</summary>
    public static string ToPayloadJson(ForegroundChange change, bool blocked) =>
        JsonSerializer.Serialize(
            new Payload(change.App.ProcessName, change.App.SignedPublisher, blocked),
            JsonOptions);

    private sealed record Payload(string ProcessName, string? Publisher, bool Blocked);
}
