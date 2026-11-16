// #91 — scan & redirect already-open tabs at session start.
//
// This is the spec the #124 acceptance criteria single out as having to FAIL
// on the unpatched code: the off-list tab is opened *before* the session
// exists, so it predates every navigation event. Only an explicit open-tab
// scan at allowlist arrival (handleSessionStarted → scanAndBlockOpenTabs) can
// catch it. Remove that call and this test times out waiting for the redirect.
//
// #489 — the session arrives from the on-box agent, over the native-messaging
// link, as soon as the agent has joined it: nothing in the browser signs in. The
// last spec hands it down while the service worker is stopped, so there is no
// link to carry it; the agent hands the current session to every connection, so
// the extension catches up as soon as it is back.

import { test, expect } from '../fixtures.ts';
import { expectRedirectedToBlockPage } from '../asserts.ts';
import { BASELINE, DASHBOARD_HOST, OFFLIST_HOST, WITH_MS365 } from '../config.ts';

test('a tab already on an off-list site is redirected when the agent hands down the session', async ({
  ext,
  agent,
  staticServer,
}) => {
  const offlistUrl = staticServer.url(OFFLIST_HOST, '/before-session');
  const page = await ext.context.newPage();
  await page.goto(offlistUrl);

  // No active session yet → the tab is left alone.
  expect(page.url()).toContain(OFFLIST_HOST);
  expect(page.url()).not.toContain('block-page.html');

  // The student joined a session with the Microsoft 365 bundle. The off-list
  // tab does not match it, so the open-tab scan must redirect it.
  const sessionId = agent.startSession(WITH_MS365);

  await expectRedirectedToBlockPage(page, ext, OFFLIST_HOST);
  // The calm, student-facing block page shows its reassuring headline
  // (AE1 / #177) — not a punitive one.
  await expect(page.locator('h1')).toContainText("Let's stay on track");

  // The block reaches the agent, which reports it to the teacher's session log.
  const report = await agent.waitForMessage((m) => m.type === 'blocked_url');
  expect(report).toMatchObject({ sessionId, url: offlistUrl, host: OFFLIST_HOST });
});

// #409 — the teacher starts a session from the dashboard, with no bundle yet.
// If their own browser enforces it, the open-tab scan must leave the dashboard
// tab alone, or they can't pick a bundle or end the session. The off-list tab
// next to it proves the scan ran.
test('a tab already on the dashboard stays open when the session starts', async ({
  ext,
  agent,
  staticServer,
}) => {
  const dashboard = await ext.context.newPage();
  await dashboard.goto(staticServer.url(DASHBOARD_HOST, '/session'));
  const offlist = await ext.context.newPage();
  await offlist.goto(staticServer.url(OFFLIST_HOST, '/before-session'));

  agent.startSession(BASELINE);

  await expectRedirectedToBlockPage(offlist, ext, OFFLIST_HOST);
  await expect(dashboard.locator('#where')).toContainText(DASHBOARD_HOST);
  expect(dashboard.url()).not.toContain('block-page.html');
});

test('a tab already on an off-list site is redirected once the extension is back, when the session started while its service worker was stopped', async ({
  ext,
  agent,
  staticServer,
}) => {
  const page = await ext.context.newPage();
  await page.goto(staticServer.url(OFFLIST_HOST, '/before-session'));

  // The agent joins while no worker, and so no link, exists to carry it.
  await ext.restartServiceWorker(async () => {
    agent.startSession(WITH_MS365, { send: false });
  });

  await expectRedirectedToBlockPage(page, ext, OFFLIST_HOST);
});
