// #93 — switching a session's bundles mid-session re-scans open tabs, so a tab
// that was on-list a moment ago gets blocked retroactively when the bundle
// that allowed it is removed (handleAllowlistReplaced → scanAndBlockOpenTabs).
//
// #426 — the other direction. A session starts with no bundles, so the
// start-of-session scan blocks tabs on hosts the teacher is about to allow.
// Adding the bundle must send those block pages back to their page, as an
// approved unblock request does; the bundle covers the host only through a
// `*.sharepoint.com` wildcard entry. Without the allowlist-amended broadcast
// from handleAllowlistReplaced the tab stays on the block page and the second
// spec times out.
//
// #73 — an approved unblock request adds its host for this student; the block
// page parked on it goes back.
//
// #489 — each change reaches the extension from the on-box agent
// (`allowlist_replaced`, `allowlist_amended`), which hears it from the backend.

import { test, expect } from '../fixtures.ts';
import { expectRedirectedToBlockPage } from '../asserts.ts';
import { BASELINE, BUNDLE_ONLY_HOST, GRANTED_HOST, OFFLIST_HOST, WITH_MS365 } from '../config.ts';

test('removing the bundle that allowed an open tab blocks it mid-session', async ({
  ext,
  agent,
  staticServer,
}) => {
  // Start with the Microsoft 365 bundle. *.sharepoint.com is in that bundle
  // but NOT the baseline, so this host is on-list only while the bundle stays
  // attached — the cleanest lever for "turn an open tab off-list".
  agent.startSession(WITH_MS365);
  await ext.waitForLog('active session cached');

  const bundleHostUrl = staticServer.url(BUNDLE_ONLY_HOST, '/sharepoint');
  const page = await ext.context.newPage();
  await page.goto(bundleHostUrl);

  // On-list while the bundle is attached → loads, not blocked.
  await expect(page.locator('#ok')).toHaveText('anchor-e2e');
  expect(page.url()).not.toContain('block-page.html');

  // Drop all bundles. The agent hands down the new allowlist; the extension
  // re-scans and finds this tab now off-list.
  agent.replaceAllowlist(BASELINE);

  await expectRedirectedToBlockPage(page, ext, BUNDLE_ONLY_HOST);
});

test('adding the bundle that covers a tab blocked at session start sends it back to its page', async ({
  ext,
  agent,
  staticServer,
}) => {
  // Both tabs are open before the session exists, the way a student's tabs
  // are when the teacher starts the lesson.
  const bundleHostUrl = staticServer.url(BUNDLE_ONLY_HOST, '/before-session');
  const covered = await ext.context.newPage();
  await covered.goto(bundleHostUrl);
  const uncovered = await ext.context.newPage();
  await uncovered.goto(staticServer.url(OFFLIST_HOST, '/before-session'));

  // No bundles yet: the baseline covers neither host, so the start-of-session
  // scan sends both tabs to the block page.
  agent.startSession(BASELINE);
  await expectRedirectedToBlockPage(covered, ext, BUNDLE_ONLY_HOST);
  await expectRedirectedToBlockPage(uncovered, ext, OFFLIST_HOST);

  // The teacher adds the Microsoft 365 bundle, whose `*.sharepoint.com` entry
  // covers the first tab's host.
  agent.replaceAllowlist(WITH_MS365);

  // Poll on *leaving* the block page: its URL carries the blocked host in its
  // `blocked` param, so asserting the host alone would pass too early. The
  // session is still running, so this is the block page releasing the tab,
  // not the session-end restore.
  await expect.poll(() => covered.url(), { timeout: 15_000 }).not.toContain('block-page.html');
  expect(covered.url()).toBe(bundleHostUrl);
  // The page really loads: the navigation filter agrees it is allowed now.
  await expect(covered.locator('#ok')).toHaveText('anchor-e2e');

  // The bundle does not cover the other tab, which stays blocked.
  expect(uncovered.url().startsWith(ext.blockPagePrefix)).toBe(true);
  await expect(uncovered.locator('[data-blocked-url]')).toContainText(OFFLIST_HOST);

  // Removing the bundle again still blocks the released tab, as before.
  agent.replaceAllowlist(BASELINE);
  await expectRedirectedToBlockPage(covered, ext, BUNDLE_ONLY_HOST);
});

test('an approved unblock request sends the tab blocked on its host back to its page', async ({
  ext,
  agent,
  staticServer,
}) => {
  const grantedUrl = staticServer.url(GRANTED_HOST, '/asked-for');
  const page = await ext.context.newPage();
  await page.goto(grantedUrl);
  agent.startSession(BASELINE);
  await expectRedirectedToBlockPage(page, ext, GRANTED_HOST);

  // The teacher approves it: the agent hands the grant down.
  agent.amendAllowlist([{ matchType: 'Suffix', value: GRANTED_HOST }]);

  await expect.poll(() => page.url(), { timeout: 15_000 }).not.toContain('block-page.html');
  expect(page.url()).toBe(grantedUrl);
  await expect(page.locator('#ok')).toHaveText('anchor-e2e');
});
