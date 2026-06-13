namespace FocusAgent.Core.Tamper;

/// <summary>
/// Reports a tamper sub-kind for the given session as a <c>TamperDetected</c>
/// event. The App-layer implementation wraps <c>ISessionHubConnection</c>; the
/// indirection keeps <see cref="ExtensionWitnessMonitor"/> free of SignalR and
/// JSON details so it can be unit-tested against a recording fake.
/// </summary>
public interface ITamperReporter
{
    Task ReportAsync(Guid sessionId, string kind, CancellationToken ct = default);
}
