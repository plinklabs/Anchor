using System.Text.Json;
using FocusAgent.Core.Realtime;
using FocusAgent.Core.Tamper;

namespace FocusAgent.App.Tamper;

/// <summary>
/// Reports an agent-witnessed tamper sub-kind as a <c>TamperDetected</c> event
/// over the session hub (#146 part 1) — the same wire path the extension uses
/// for its in-browser kinds (#105). The payload is <c>{"kind":"…"}</c>, which the
/// backend stores and surfaces verbatim; no server-side change is needed for a
/// new kind. Best-effort by nature: <c>ReportEventAsync</c> no-ops when the
/// transport isn't connected, mirroring the extension's reportTamper.
/// </summary>
public sealed class SignalRTamperReporter : ITamperReporter
{
    // Must parse (case-insensitive) to Anchor.Domain.Events.EventKind.TamperDetected
    // on the backend's ReportEvent. The agent doesn't reference the backend enum.
    private const string TamperDetectedKind = "TamperDetected";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ISessionHubConnection _hub;
    private readonly TimeProvider _clock;

    public SignalRTamperReporter(ISessionHubConnection hub, TimeProvider clock)
    {
        _hub = hub;
        _clock = clock;
    }

    public Task ReportAsync(Guid sessionId, string kind, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new { kind }, JsonOptions);
        return _hub.ReportEventAsync(sessionId, TamperDetectedKind, json, _clock.GetUtcNow(), ct);
    }
}
