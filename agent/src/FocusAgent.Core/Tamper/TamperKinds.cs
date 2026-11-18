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
    /// part 1). The agent is the on-box witness, because the extension cannot
    /// witness its own disablement: it observes the native-messaging link drop
    /// (the browser tears the host down when the extension goes away). The link
    /// drops just the same when the extension restarts or the browser or a
    /// profile is closed, so this kind is reported only once the extension has
    /// stayed gone past the grace period with the browser open, or is switched
    /// off in a profile that is still open (#518, <see cref="ExtensionGuard"/>).
    /// </summary>
    public const string ExtensionDisabled = "extension_disabled";

    /// <summary>
    /// An Edge InPrivate window was still open
    /// <see cref="Settings.SessionSettings.SchoolProfileReportSeconds"/> after it
    /// opened during an active session (#148, #535). The extension only ever sees
    /// InPrivate windows once it has been allowed in InPrivate — which is also the
    /// case where it still filters them — so the reliable signal is the agent's:
    /// it enumerates Edge windows and recognises an InPrivate one by its title even
    /// when the extension has no incognito access
    /// (<see cref="InPrivateWitnessMonitor"/>). Since #525 such a window is paused
    /// at once and its overlay offers to close it, so, as for
    /// <see cref="OutsideSchoolProfile"/>, the teacher hears of it only when the
    /// student leaves it open. The extension still sends this string itself
    /// (<c>classifyCreatedWindow</c> in extension/src/shared/tamper.ts) the moment
    /// it sees one, but the agent doesn't pass that on (#535).
    /// </summary>
    public const string InPrivateOpened = "inprivate_opened";

    /// <summary>
    /// The extension's "Site access" was turned down from "On all sites" during
    /// a session (#105). The extension used to report it itself the moment it saw
    /// the change; since #524 it tells the agent what its settings allow, and
    /// <see cref="ExtensionSelfCheckWatch"/> reports this kind only when the
    /// student hasn't put it back within
    /// <see cref="Settings.SessionSettings.ExtensionSiteAccessReportSeconds"/> of
    /// the extension saying so, whether it was changed before the session or
    /// during it. An extension older than #524 still reports it itself, under the
    /// same string.
    /// </summary>
    public const string HostPermissionRevoked = "host_permission_revoked";

    /// <summary>
    /// During a session a window of another Edge profile, or a Guest window, stayed
    /// open past <see cref="Settings.SessionSettings.SchoolProfileReportSeconds"/>
    /// (#525). Only the profile signed in with the school account may be used, so
    /// the agent pauses such a window at once and its overlay offers to close it;
    /// the teacher hears of it only when the student leaves it open. An InPrivate
    /// window is paused the same way but reported as <see cref="InPrivateOpened"/>.
    /// </summary>
    public const string OutsideSchoolProfile = "outside_school_profile";

    /// <summary>
    /// During a session no Edge profile was signed in with the student's school
    /// account for longer than
    /// <see cref="Settings.SessionSettings.SchoolProfileReportSeconds"/> (#525).
    /// Edge is paused meanwhile, with a message naming the account to sign in with.
    /// </summary>
    public const string SchoolProfileMissing = "school_profile_missing";

    /// <summary>
    /// During a session the Edge profile signed in with the student's school
    /// account stayed without Anchor's extension for longer than
    /// <see cref="Settings.SessionSettings.SchoolProfileReportSeconds"/> (#536).
    /// Edge is paused meanwhile, with a message that Anchor still has to be added
    /// to that profile and a button that opens the store listing in it. Signing in
    /// and adding the extension are two steps, each with the report time of its
    /// own, so the teacher hears of the one the student left undone.
    /// </summary>
    public const string SchoolProfileWithoutExtension = "school_profile_without_extension";
}
