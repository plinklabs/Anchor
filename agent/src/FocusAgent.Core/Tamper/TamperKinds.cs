namespace FocusAgent.Core.Tamper;

/// <summary>
/// Sub-kinds the agent reports inside a <c>TamperDetected</c> event's payload
/// (<c>{"kind":"…"}</c>), reported via the hub's <c>ReportEvent</c> exactly like
/// the extension's in-browser kinds (#105). The backend stores and surfaces the
/// string verbatim — there is no server-side enum to keep in sync — and the
/// dashboard's tamper flag (#105) counts any <c>TamperDetected</c> regardless of
/// sub-kind, so a new kind here surfaces with no backend or dashboard change.
/// </summary>
public static class TamperKinds
{
    /// <summary>
    /// The extension was disabled or removed during an active session (#146
    /// part 1). The agent is the on-box witness: it observes the native-messaging
    /// link drop (the browser tears the host down when the extension goes away)
    /// and reports this kind, because the extension cannot witness its own
    /// disablement.
    /// </summary>
    public const string ExtensionDisabled = "extension_disabled";
}
