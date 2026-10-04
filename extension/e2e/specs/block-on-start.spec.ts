// #91 — scan & redirect already-open tabs at session start.
//
// This is the spec the #124 acceptance criteria single out as having to FAIL
// on the unpatched code: the off-list tab is opened *before* the session
// exists, so it predates every navigation event. Only an explicit open-tab
// scan at allowlist arrival (handleSessionStarted → scanAndBlockOpenTabs) can
// catch it. Remove that call and this test times out waiting for the redirect.
//
// #356 — the session starts straight after the extension reports its hub
// connection established, which can be before the backend has added the
// connection to the student's user group, the group SessionStarted goes to.
// The second spec starts it while the service worker is stopped, so no
// connection exists to receive it at all. Either way the extension learns of
// it by asking the backend once it is connected.

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
  // The calm, student-facing block page shows its reassuring headline
  // (AE1 / #177) — not a punitive one.
  await expect(page.locator('h1')).toContainText("Let's stay on track");
});

test('a tab already on an off-list site is redirected once the extension is back, when the session started while its service worker was stopped', async ({
  ext,
  backend,
  staticServer,
}) => {
  await ext.configure();

  const page = await ext.context.newPage();
  await page.goto(staticServer.url(OFFLIST_HOST, '/before-session'));

  const classId = await backend.findClassId();
  const bundleId = await backend.findBundleId();
  // The SessionStarted broadcast goes out while no worker, and so no hub
  // connection, exists to receive it.
  await ext.restartServiceWorker(async () => {
    await backend.startSession(classId, [bundleId]);
  });

  await expectRedirectedToBlockPage(page, ext, OFFLIST_HOST);
});
