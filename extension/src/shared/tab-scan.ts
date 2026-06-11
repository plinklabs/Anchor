// Decide which already-open tabs must be redirected to the block page when a
// session's allowlist arrives. chrome.webNavigation / tabs.onUpdated only fire
// on *new* navigations, so a page loaded before the session started is never
// re-evaluated — a student can open a game site first and keep playing once
// the session begins (#91). Scanning chrome.tabs.query({}) on allowlist
// arrival closes that loophole.
//
// The decision is pure (no chrome APIs) so it can be unit-tested without a
// browser harness, mirroring how host-matcher and session-state split their
// pure logic from the thin chrome glue in background.ts.

import { isUrlAllowed, type AllowedDomain } from './host-matcher';

/** The slice of chrome.tabs.Tab the scan actually reads. */
export interface ScannableTab {
  id?: number;
  url?: string;
  pendingUrl?: string;
}

/** A tab the scan decided to redirect, paired with the URL it was showing. */
export interface TabToBlock {
  tabId: number;
  url: string;
}

/**
 * Given every open tab and the active allowlist, return the tabs whose current
 * URL is off-list and must therefore be redirected to the block page.
 *
 * Skipped, never returned:
 * - tabs without a usable id (e.g. devtools detached panels report id < 0);
 * - tabs whose URL we can't read (`url`/`pendingUrl` both empty — the host
 *   permission may not cover them yet, or the tab is still a blank new tab);
 * - extension-internal pages (the block page, options) — redirecting those
 *   would loop the block page back onto itself;
 * - URLs already on the allowlist, and non-http(s) URLs (isUrlAllowed lets
 *   chrome:, edge:, file:, about:, … through — those aren't navigable content
 *   to police).
 *
 * `extensionBaseUrl` is chrome.runtime.getURL('') passed in by the caller so
 * this stays free of chrome globals.
 */
export function selectTabsToBlock(
  tabs: ReadonlyArray<ScannableTab>,
  domains: ReadonlyArray<AllowedDomain>,
  extensionBaseUrl: string,
): TabToBlock[] {
  const toBlock: TabToBlock[] = [];
  for (const tab of tabs) {
    if (typeof tab.id !== 'number' || tab.id < 0) continue;

    // pendingUrl covers a tab mid-navigation whose committed `url` is still the
    // previous page; we want to judge where it's heading.
    const url = tab.url || tab.pendingUrl;
    if (!url) continue;

    if (extensionBaseUrl && url.startsWith(extensionBaseUrl)) continue;
    if (isUrlAllowed(url, domains)) continue;

    toBlock.push({ tabId: tab.id, url });
  }
  return toBlock;
}
