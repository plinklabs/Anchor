// Decide whether an open block page may send the student back to the page it
// blocked. The background broadcasts the allowlist in force whenever it changes
// mid-session: after an approved unblock request (#73), and after the teacher
// changes the session's bundles (#426). A session starts with no bundles, so
// its first scan blocks tabs on hosts the teacher allows seconds later; those
// block pages have to notice and go back on their own.
//
// The block page judges with the same matcher as the navigation filter, so
// Wildcard (`*.smartschool.be`), Suffix and Exact entries mean exactly what
// they mean there. A page it releases is one the filter then lets through,
// not one it bounces straight back to the block page.
//
// Pure (no DOM, no chrome APIs) so it can be unit-tested without a browser,
// like tab-scan and tab-restore.

import { isUrlAllowed, type AllowedDomain } from './host-matcher';

/** What a block page knows about itself, read off its own URL. */
export interface BlockedPage {
  blockedUrl: string;
  sessionId: string | null;
}

/**
 * True when `domains`, the allowlist now in force for `sessionId`, covers the
 * URL this block page is holding back.
 *
 * Never true for:
 * - a page without a session, or one from another session — a broadcast for
 *   another session says nothing about this one;
 * - a blocked URL that is missing or isn't http(s). The filter only ever
 *   blocks http(s), and isUrlAllowed waves every other scheme through, so
 *   without this check a crafted `blocked=javascript:…` would count as allowed.
 */
export function isBlockLifted(
  page: BlockedPage,
  sessionId: string,
  domains: ReadonlyArray<AllowedDomain>,
): boolean {
  if (!page.sessionId || page.sessionId !== sessionId) return false;
  if (!isWebUrl(page.blockedUrl)) return false;
  return isUrlAllowed(page.blockedUrl, domains);
}

function isWebUrl(candidate: string): boolean {
  let parsed: URL;
  try {
    parsed = new URL(candidate);
  } catch {
    return false;
  }
  return parsed.protocol === 'http:' || parsed.protocol === 'https:';
}
