// The extension's check of its own settings (#524). The chrome.* wiring lives in
// background.ts; the decision logic lives here so it can be unit-tested without a
// chrome shim — same split as tamper.ts / host-matcher.ts.
//
// A student can change some of Anchor's settings on its page in Edge
// (edge://extensions → Anchor → Details) before a session starts. Nothing fires
// then, the extension stays enabled and its link to the agent stays up, so
// without a check the agent counted it as healthy. So on every session the agent
// hands down (`session_started`, sent when the student joins and again on every
// reconnect), and whenever its permissions change, the extension reads its own
// state and sends it to the agent (`self_check`), which decides what to do.
//
// What a student can change there (Chromium's extension settings, which Edge
// shares):
//
// - "Site access" (On click / On specific sites / On all sites). Locked on a
//   policy-installed extension (Chromium never withholds a policy extension's host
//   permissions), changeable on one installed from the store. Changing it fires
//   chrome.permissions.onRemoved / onAdded without restarting the extension.
//   `allSites` is false while it isn't "On all sites"; the agent pauses Edge then.
// - "Allow in InPrivate". Changeable on every install, and OFF by default on every
//   install, policy ones included (no Edge policy turns it on). Without it the
//   extension doesn't run in InPrivate windows, so those aren't filtered. Changing
//   it restarts the extension, which reconnects and is handed the session again,
//   so the check runs again. `inPrivate` is reported for the agent to know; it
//   pauses nothing, because an untouched laptop fails it, and InPrivate windows
//   are paused during a session on their own (#525).
// - "Allow access to file URLs". Doesn't matter: file: pages are never filtered.
// - Switching the extension off or removing it: locked on a policy install; the
//   agent sees the link drop (#427, #518).

/** What the extension reads about its own settings. */
export interface SelfCheckResult {
  /** It may work on every site: "Site access" is "On all sites". */
  allSites: boolean;
  /** It is allowed in InPrivate windows: "Allow in InPrivate" is on. */
  inPrivate: boolean;
}

/** The chrome calls the check makes, injected so it runs without a browser in tests. */
export interface SelfCheckApi {
  /** `chrome.permissions.contains(ALL_SITES)`. */
  containsAllSites(): Promise<boolean>;
  /** `chrome.extension.isAllowedIncognitoAccess()`. */
  isAllowedInPrivate(): Promise<boolean>;
}

/** The host access the extension's manifest asks for, and the check looks for. */
export const ALL_SITES = { origins: ['<all_urls>'] };

/**
 * Reads the extension's settings. A setting that can't be read counts as fine:
 * pausing Edge on a guess would punish a student who changed nothing.
 */
export async function runSelfCheck(
  api: SelfCheckApi,
  onError?: (setting: string, err: unknown) => void,
): Promise<SelfCheckResult> {
  return {
    allSites: await readSetting(() => api.containsAllSites(), 'site access', onError),
    inPrivate: await readSetting(() => api.isAllowedInPrivate(), 'InPrivate access', onError),
  };
}

async function readSetting(
  read: () => Promise<boolean>,
  setting: string,
  onError?: (setting: string, err: unknown) => void,
): Promise<boolean> {
  try {
    return (await read()) !== false;
  } catch (err) {
    onError?.(setting, err);
    return true;
  }
}

/**
 * The extension's own page in the browser's extension settings, where "Site
 * access" and "Allow in InPrivate" are. The `chrome://` form works in Chrome and
 * in Edge, which opens it as `edge://extensions/?id=…`; an extension can open it
 * with chrome.tabs.create, which the agent can't do from outside the browser.
 */
export function extensionPageUrl(extensionId: string): string {
  return `chrome://extensions/?id=${extensionId}`;
}
