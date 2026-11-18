// #531 — Microsoft 365 on the web moved to Microsoft's newer domains: its home
// page is m365.cloud.microsoft now (www.office.com and www.microsoft365.com
// redirect there, and sign-in returns to www.microsoft365.com), and Outlook,
// Teams, Word, Forms ... have *.cloud.microsoft hosts. The Microsoft 365 bundle
// listed neither, so a student in a session with it got the block page on the
// Microsoft 365 home page. The bundle lists *.cloud.microsoft and
// *.microsoft365.com now; like the rest of Microsoft's web, they stay closed in
// a session without it (#516).
//
// #532 — the same goes for the shortcuts a student types: outlook.com and
// onedrive.com (and their www. hosts) redirect on to Outlook and OneDrive, but
// the extension judges a navigation by its first URL, so the shortcut got the
// block page even with the bundle in the session. The bundle lists them as exact
// entries now.
//
// WITH_MS365 is the bundle as the seeds insert it. Backend CI checks that the
// migration brings a deployment's existing bundle to the same list.

import { test, expect } from '../fixtures.ts';
import { expectRedirectedToBlockPage } from '../asserts.ts';
import {
  BASELINE,
  MICROSOFT_365_HOSTS,
  MICROSOFT_365_SHORTCUTS,
  WITH_MS365,
} from '../config.ts';
import { redirectOverHttps, serveOverHttps } from '../static-server.ts';

test.beforeEach(async ({ ext, agent }) => {
  await serveOverHttps(ext.context, MICROSOFT_365_HOSTS);
  agent.startSession(BASELINE);
  await ext.waitForLog('active session cached');
});

test('Microsoft 365 on its newer domains opens once the teacher picks Microsoft 365', async ({
  ext,
  agent,
}) => {
  for (const host of MICROSOFT_365_HOSTS) {
    const page = await ext.context.newPage();
    // Superseded by the redirect to the block page; the rejection is expected.
    await page.goto(`https://${host}/during-session`).catch(() => {});
    await expectRedirectedToBlockPage(page, ext, host);
    await page.close();
  }

  // The teacher adds the Microsoft 365 bundle: the same hosts open.
  agent.replaceAllowlist(WITH_MS365);
  await ext.waitForLog('active session allowlist replaced');

  const page = await ext.context.newPage();
  for (const host of MICROSOFT_365_HOSTS) {
    await page.goto(`https://${host}/with-ms365`);

    // The page itself loads, so the filter let the navigation through.
    await expect(page.locator('#where'), host).toContainText(`${host}/with-ms365`);
    expect(page.url(), host).not.toContain('block-page.html');
  }
});

test('a typed outlook.com or onedrive.com leads on to Outlook or OneDrive once the teacher picks Microsoft 365', async ({
  ext,
  agent,
  staticServer,
}) => {
  const shortcuts = MICROSOFT_365_SHORTCUTS.map((s) => s.host);
  await serveOverHttps(ext.context, shortcuts);
  for (const host of shortcuts) {
    const page = await ext.context.newPage();
    // Superseded by the redirect to the block page; the rejection is expected.
    await page.goto(`https://${host}/`).catch(() => {});
    await expectRedirectedToBlockPage(page, ext, host);
    await page.close();
  }

  // The teacher adds the Microsoft 365 bundle. From here on each shortcut
  // answers as Microsoft's does: with a redirect on to the app's host.
  agent.replaceAllowlist(WITH_MS365);
  await ext.waitForLog('active session allowlist replaced');
  await redirectOverHttps(
    ext.context,
    Object.fromEntries(
      MICROSOFT_365_SHORTCUTS.map(({ host, redirectsTo }) => [
        host,
        staticServer.url(redirectsTo, `/from/${host}`),
      ]),
    ),
  );

  const page = await ext.context.newPage();
  for (const { host, redirectsTo } of MICROSOFT_365_SHORTCUTS) {
    await page.goto(`https://${host}/`);

    // The student lands where the shortcut leads: neither the shortcut nor
    // its target got the block page.
    await expect(page.locator('#where'), host).toContainText(`${redirectsTo}:${staticServer.port}/from/${host}`);
    expect(page.url(), host).not.toContain('block-page.html');
  }
});
