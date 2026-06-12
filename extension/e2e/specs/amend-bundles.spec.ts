// #93 — switching a session's bundles mid-session re-scans open tabs, so a tab
// that was on-list a moment ago gets blocked retroactively when the bundle
// that allowed it is removed (handleSessionBundlesUpdated → scanAndBlockOpenTabs).

import { test, expect } from '../fixtures.ts';
import { expectRedirectedToBlockPage } from '../asserts.ts';
import { BUNDLE_ONLY_HOST } from '../config.ts';

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
