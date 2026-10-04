using FocusAgent.Core.Focus;
using FocusAgent.Core.Realtime;

namespace FocusAgent.App.Focus;

/// <summary>
/// Reports foreground switches as <c>ForegroundChange</c> events over the
/// session hub. The payload — app name, publisher, blocked — is built by
/// <see cref="ForegroundChangeReport"/>, which deliberately leaves out the
/// window title and executable path (#345).
/// </summary>
public sealed class SignalRFocusEventReporter : IFocusEventReporter
{
    private readonly ISessionHubConnection _hub;
    private readonly TimeProvider _clock;

    public SignalRFocusEventReporter(ISessionHubConnection hub, TimeProvider clock)
    {
        _hub = hub;
        _clock = clock;
    }

    public Task ReportForegroundChangeAsync(Guid sessionId, ForegroundChange change, bool blocked, CancellationToken ct = default) =>
        _hub.ReportEventAsync(
            sessionId,
            ForegroundChangeReport.Kind,
            ForegroundChangeReport.ToPayloadJson(change, blocked),
            _clock.GetUtcNow(),
            ct);
}
