// #93 — switching a session's bundles mid-session re-scans open tabs, so a tab
// that was on-list a moment ago gets blocked retroactively when the bundle
// that allowed it is removed (handleSessionBundlesUpdated → scanAndBlockOpenTabs).
//
// #426 — the other direction. A session starts with no bundles, so the
// start-of-session scan blocks tabs on hosts the teacher is about to allow.
// Adding the bundle must send those block pages back to their page, as an
// approved unblock request does; the bundle covers the host only through a
// `*.sharepoint.com` wildcard entry. Without the allowlist-amended broadcast
// from handleSessionBundlesUpdated the tab stays on the block page and the
// second spec times out.

import { test, expect } from '../fixtures.ts';
import { expectRedirectedToBlockPage } from '../asserts.ts';
import { BUNDLE_ONLY_HOST, OFFLIST_HOST } from '../config.ts';

test('removing the bundle that allowed an open tab blocks it mid-session', async ({
  ext,
  backend,
  staticServer,
}) => {
  await ext.configure();

  // Start with the Microsoft 365 bundle. *.sharepoint.com is in that bundle
  // but NOT the baseline, so this host is on-list only while the bundle stays
  // attached — the cleanest lever for "turn an open tab off-list".
  const classId = await backend.findClassId();
  const bundleId = await backend.findBundleId();
  const session = await backend.startSession(classId, [bundleId]);
  await ext.waitForLog('active session cached');

  const bundleHostUrl = staticServer.url(BUNDLE_ONLY_HOST, '/sharepoint');
  const page = await ext.context.newPage();
  await page.goto(bundleHostUrl);

  // On-list while the bundle is attached → loads, not blocked.
  await expect(page.locator('#ok')).toHaveText('anchor-e2e');
  expect(page.url()).not.toContain('block-page.html');

  // Drop all bundles. The backend pushes SessionBundlesUpdated to the joined
  // student; the extension re-scans and finds this tab now off-list.
  await backend.updateBundles(session.id, []);

  await expectRedirectedToBlockPage(page, ext, BUNDLE_ONLY_HOST);
});

test('adding the bundle that covers a tab blocked at session start sends it back to its page', async ({
  ext,
  backend,
  staticServer,
}) => {
  await ext.configure();

  // Both tabs are open before the session exists, the way a student's tabs
  // are when the teacher starts the lesson.
  const bundleHostUrl = staticServer.url(BUNDLE_ONLY_HOST, '/before-session');
  const covered = await ext.context.newPage();
  await covered.goto(bundleHostUrl);
  const uncovered = await ext.context.newPage();
  await uncovered.goto(staticServer.url(OFFLIST_HOST, '/before-session'));

  // No bundles yet: the baseline covers neither host, so the start-of-session
  // scan sends both tabs to the block page.
  const classId = await backend.findClassId();
  const bundleId = await backend.findBundleId();
  const session = await backend.startSession(classId, []);
  await expectRedirectedToBlockPage(covered, ext, BUNDLE_ONLY_HOST);
  await expectRedirectedToBlockPage(uncovered, ext, OFFLIST_HOST);

  // The teacher adds the Microsoft 365 bundle, whose `*.sharepoint.com` entry
  // covers the first tab's host.
  await backend.updateBundles(session.id, [bundleId]);

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
  await backend.updateBundles(session.id, []);
  await expectRedirectedToBlockPage(covered, ext, BUNDLE_ONLY_HOST);
});
