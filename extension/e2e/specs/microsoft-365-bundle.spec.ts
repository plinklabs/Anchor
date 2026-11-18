// #531 — Microsoft 365 on the web moved to Microsoft's newer domains: its home
// page is m365.cloud.microsoft now (www.office.com and www.microsoft365.com
// redirect there, and sign-in returns to www.microsoft365.com), and Outlook,
// Teams, Word, Forms ... have *.cloud.microsoft hosts. The Microsoft 365 bundle
// listed neither, so a student in a session with it got the block page on the
// Microsoft 365 home page. The bundle lists *.cloud.microsoft and
// *.microsoft365.com now; like the rest of Microsoft's web, they stay closed in
// a session without it (#516).
//
// WITH_MS365 is the bundle as the seeds insert it. Backend CI checks that the
// migration brings a deployment's existing bundle to the same list.

import { test, expect } from '../fixtures.ts';
import { expectRedirectedToBlockPage } from '../asserts.ts';
import { BASELINE, MICROSOFT_365_HOSTS, WITH_MS365 } from '../config.ts';
import { serveOverHttps } from '../static-server.ts';

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
