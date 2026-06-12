// #91 — scan & redirect already-open tabs at session start.
//
// This is the spec the #124 acceptance criteria single out as having to FAIL
// on the unpatched code: the off-list tab is opened *before* the session
// exists, so it predates every navigation event. Only an explicit open-tab
// scan at allowlist arrival (handleSessionStarted → scanAndBlockOpenTabs) can
// catch it. Remove that call and this test times out waiting for the redirect.

import { test, expect } from '../fixtures.ts';
import { expectRedirectedToBlockPage } from '../asserts.ts';
import { OFFLIST_HOST } from '../config.ts';

test('a tab already on an off-list site is redirected when the session starts', async ({
  ext,
  backend,
  staticServer,
}) => {
  await ext.configure();

  const offlistUrl = staticServer.url(OFFLIST_HOST, '/before-session');
  const page = await ext.context.newPage();
  await page.goto(offlistUrl);

  // No active session yet → the tab is left alone.
  expect(page.url()).toContain(OFFLIST_HOST);
  expect(page.url()).not.toContain('block-page.html');

  // Start a session for the seeded class with the Microsoft 365 bundle. The
  // off-list tab does not match it, so the open-tab scan must redirect it.
  const classId = await backend.findClassId();
  const bundleId = await backend.findBundleId();
  await backend.startSession(classId, [bundleId]);

  await expectRedirectedToBlockPage(page, ext, OFFLIST_HOST);
  // The friendly block page shows its "this site isn't allowed" heading.
  await expect(page.locator('h1')).toContainText("isn't allowed");
});
