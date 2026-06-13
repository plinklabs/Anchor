// Decide which block-page tabs to navigate back to their original URL when a
// session ends (#121). When a session is active, off-allowlist tabs are
// redirected to block-page.html?blocked=<encoded original>&session=<id> (#91,
// #72). Once the session is over those tabs would otherwise sit on the block
// page until the student clicks "Go back" — confusing, since enforcement is no
// longer in effect. The original URL is already encoded in the block-page URL,
// so restoration is stateless: read `blocked` straight back off the tab's URL.
//
// The decision is pure (no chrome APIs) so it can be unit-tested without a
// browser harness, mirroring how tab-scan and host-matcher split their pure
// logic from the thin chrome glue in background.ts.

/** The slice of chrome.tabs.Tab the restore scan actually reads. */
export interface RestorableTab {
  id?: number;
  url?: string;
}

/** A tab to restore, paired with the original URL it should return to. */
export interface TabToRestore {
  tabId: number;
  url: string;
}

/**
 * Given every open tab, return the block-page tabs that Anchor redirected for
 * the just-ended session, each paired with the original URL to navigate back to.
 *
 * Skipped, never returned:
 * - tabs without a usable id;
 * - tabs not currently on the extension block page;
 * - tabs whose `session` param doesn't match the ended session — don't
 *   resurrect a stale block page left over from an earlier session;
 * - tabs whose `blocked` param is missing or doesn't parse to an http(s) URL —
 *   leave them on the block page rather than navigating somewhere wrong.
 *
 * `extensionBaseUrl` is chrome.runtime.getURL('') and `blockPageFile` is the
 * block-page filename, passed in by the caller so this stays free of chrome
 * globals.
 */
export function selectTabsToRestore(
  tabs: ReadonlyArray<RestorableTab>,
  extensionBaseUrl: string,
  blockPageFile: string,
  endedSessionId: string,
): TabToRestore[] {
  if (!extensionBaseUrl || !endedSessionId) return [];
  const blockPagePrefix = extensionBaseUrl + blockPageFile;

  const toRestore: TabToRestore[] = [];
  for (const tab of tabs) {
    if (typeof tab.id !== 'number' || tab.id < 0) continue;
    if (!tab.url || !tab.url.startsWith(blockPagePrefix)) continue;

    let params: URLSearchParams;
    try {
      params = new URL(tab.url).searchParams;
    } catch {
      continue;
    }

    if (params.get('session') !== endedSessionId) continue;

    const original = params.get('blocked');
    if (!original || !isRestorableUrl(original)) continue;

    toRestore.push({ tabId: tab.id, url: original });
  }
  return toRestore;
}

/**
 * Only navigate back to a real http(s) page. A missing or junk `blocked` value
 * (or a non-web scheme that could be abused) is left on the block page.
 */
function isRestorableUrl(candidate: string): boolean {
  let parsed: URL;
  try {
    parsed = new URL(candidate);
  } catch {
    return false;
  }
  return parsed.protocol === 'http:' || parsed.protocol === 'https:';
}
