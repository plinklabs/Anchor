// Pure tamper-classification helpers (#105). The chrome.* event wiring lives in
// background.ts; the decision logic lives here so it can be unit-tested without a
// chrome shim — same split as host-matcher / tab-scan / session-state.
//
// Soft-enforcement posture (design §5.4): we cannot *prevent* a student from
// reconfiguring or sidestepping the extension via Edge's own extension-management
// page (edge://extensions — site access, Allow in InPrivate, disable, remove),
// so we make the attempt *visible* to the teacher instead. These are the signals
// the extension can witness while it is still running; the robust ones
// (extension disabled/removed, InPrivate when the extension has no incognito
// access) come from the agent acting as on-box witness in a follow-up.

import type { TamperKind } from './types';

/**
 * Classify a newly-created window. An InPrivate (incognito) window is the
 * student stepping outside the URL filter. Best-effort: the extension only sees
 * incognito windows once it's been allowed in InPrivate — which is also the case
 * where it still filters them — so the reliable InPrivate signal is the agent's.
 * Returns the kind to report, or null for an ordinary window.
 */
export function classifyCreatedWindow(
  window: { incognito?: boolean } | null | undefined,
): TamperKind | null {
  return window?.incognito === true ? 'inprivate_opened' : null;
}

/**
 * True when a permissions removal means we lost host access we need to filter
 * URLs. Downgrading "Site access" from "On all sites" to "On click" / "On
 * specific sites" in edge://extensions revokes the broad host permission; with
 * it withheld the navigation filter goes blind, so the loss is itself the
 * tamper signal. A removal carrying only API permissions (no origins) is not.
 */
export function isHostAccessLoss(
  removed: { origins?: readonly string[] } | null | undefined,
): boolean {
  return (removed?.origins?.length ?? 0) > 0;
}
