// #516 — what every session allows before the teacher picks a bundle: the
// hosts the Microsoft / school sign-in flow navigates through, so a session
// can't lock a student out of signing in, and no more of Microsoft's web.
// Copilot, Office and Outlook used to be open in every session through the
// baseline's *.microsoft.com, *.office.com, *.live.com ... wildcards; they come
// with the Microsoft 365 bundle now. The Google Fonts entries went as well: a
// page's fonts are subresources, which the extension doesn't filter.
//
// The session is the one the backend sends without bundles: its baseline read
// from contracts/session-baseline.json, which Backend CI holds to the backend's
// list, plus the dashboard (#409).

import { test, expect } from '../fixtures.ts';
import { expectRedirectedToBlockPage } from '../asserts.ts';
import {
  BASELINE,
  BASELINE_ONLIST_HOST,
  MICROSOFT_WEB_HOSTS,
  SIGN_IN_HOSTS,
  WITH_MS365,
} from '../config.ts';
import { WEB_FONT_FAMILY, WEB_FONT_PAGE_PATH } from '../static-server.ts';

test.beforeEach(async ({ ext, agent }) => {
  agent.startSession(BASELINE);
  await ext.waitForLog('active session cached');
});

test('a student can still reach every host the sign-in flow goes through', async ({
  ext,
  staticServer,
}) => {
  const page = await ext.context.newPage();
  for (const host of SIGN_IN_HOSTS) {
    await page.goto(staticServer.url(host, '/sign-in'));

    // The page itself loads, so the filter let the navigation through.
    await expect(page.locator('#where'), host).toContainText(host);
    expect(page.url(), host).not.toContain('block-page.html');
  }
});

test('the rest of Microsoft\'s web stays blocked until the teacher picks Microsoft 365', async ({
  ext,
  agent,
  staticServer,
}) => {
  for (const host of MICROSOFT_WEB_HOSTS) {
    const page = await ext.context.newPage();
    // Superseded by the redirect to the block page; the rejection is expected.
    await page.goto(staticServer.url(host, '/during-session')).catch(() => {});
    await expectRedirectedToBlockPage(page, ext, host);
    await page.close();
  }

  // The teacher adds the Microsoft 365 bundle: the same hosts open.
  agent.replaceAllowlist(WITH_MS365);
  await ext.waitForLog('active session allowlist replaced');

  const page = await ext.context.newPage();
  for (const host of MICROSOFT_WEB_HOSTS) {
    await page.goto(staticServer.url(host, '/with-ms365'));
    await expect(page.locator('#where'), host).toContainText(host);
    expect(page.url(), host).not.toContain('block-page.html');
  }
});

test('a page on an allowed site still gets its Google Fonts, which no session lists', async ({
  ext,
  staticServer,
}) => {
  const page = await ext.context.newPage();
  await page.goto(staticServer.url(BASELINE_ONLIST_HOST, WEB_FONT_PAGE_PATH));
  await expect(page.locator('#ok')).toHaveText('anchor-e2e');

  // The @font-face exists only once the stylesheet from fonts.googleapis.com
  // has loaded, and is "loaded" only once its file from fonts.gstatic.com has.
  const statuses = await page.evaluate(async (family) => {
    await document.fonts.load(`16px "${family}"`).catch(() => []);
    return [...document.fonts]
      .filter((face) => face.family.replace(/["']/g, '') === family)
      .map((face) => face.status);
  }, WEB_FONT_FAMILY);
  expect(statuses).toEqual(['loaded']);
  expect(page.url()).not.toContain('block-page.html');
});
