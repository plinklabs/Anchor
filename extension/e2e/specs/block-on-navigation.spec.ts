// #72 — live navigation filtering during an active session: off-list
// navigations are redirected to the block page; on-list ones are left alone.
// The "untouched" half is the regression guard that the filter doesn't
// over-block (which would break logins, the dashboard, etc.).

import { test, expect } from '../fixtures.ts';
import { expectRedirectedToBlockPage } from '../asserts.ts';
import { BASELINE_ONLIST_HOST, OFFLIST_HOST } from '../config.ts';

test.beforeEach(async ({ ext, backend }) => {
  await ext.configure();
  const classId = await backend.findClassId();
  const bundleId = await backend.findBundleId();
  await backend.startSession(classId, [bundleId]);
  // Wait until the extension has cached the allowlist, so navigations are
  // judged against a live session rather than the idle (never-block) state.
  await ext.waitForLog('active session cached');
});

test('navigating to an off-list site is redirected to the block page', async ({
  ext,
  staticServer,
}) => {
  const offlistUrl = staticServer.url(OFFLIST_HOST, '/during-session');
  const page = await ext.context.newPage();
  // The redirect fires in onBeforeNavigate, so this goto is superseded — its
  // rejection is expected and irrelevant; the resulting URL is what matters.
  await page.goto(offlistUrl).catch(() => {});

  await expectRedirectedToBlockPage(page, ext, OFFLIST_HOST);
});

test('navigating to an on-list site is left untouched', async ({ ext, staticServer }) => {
  // 127.0.0.1 is always allowed in a Development build (the #125 dev carve-out).
  const onlistUrl = staticServer.url(BASELINE_ONLIST_HOST, '/allowed');
  const page = await ext.context.newPage();
  await page.goto(onlistUrl);

  // Give any (erroneous) redirect a beat to happen, then assert it didn't.
  await expect(page.locator('#ok')).toHaveText('anchor-e2e');
  expect(page.url()).toContain(BASELINE_ONLIST_HOST);
  expect(page.url()).not.toContain('block-page.html');
});
