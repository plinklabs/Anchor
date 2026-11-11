// #72 — live navigation filtering during an active session: off-list
// navigations are redirected to the block page; on-list ones are left alone.
// The "untouched" half is the regression guard that the filter doesn't
// over-block (which would break logins, the dashboard, etc.).
//
// #489 — the extension reports each block to the on-box agent, which sends it
// to the teacher with its own connection; and with the agent gone, the session
// it handed down stays in force rather than leaving Edge unfiltered.

import { test, expect } from '../fixtures.ts';
import { expectRedirectedToBlockPage } from '../asserts.ts';
import { BASELINE_ONLIST_HOST, DASHBOARD_HOST, OFFLIST_HOST, WITH_MS365 } from '../config.ts';

let sessionId: string;

test.beforeEach(async ({ ext, agent }) => {
  sessionId = agent.startSession(WITH_MS365);
  // Wait until the extension has cached the allowlist, so navigations are
  // judged against a live session rather than the idle (never-block) state.
  await ext.waitForLog('active session cached');
});

test('navigating to an off-list site is redirected to the block page, and the agent hears of it', async ({
  ext,
  agent,
  staticServer,
}) => {
  const offlistUrl = staticServer.url(OFFLIST_HOST, '/during-session');
  const page = await ext.context.newPage();
  // The redirect fires in onBeforeNavigate, so this goto is superseded — its
  // rejection is expected and irrelevant; the resulting URL is what matters.
  await page.goto(offlistUrl).catch(() => {});

  await expectRedirectedToBlockPage(page, ext, OFFLIST_HOST);
  const report = await agent.waitForMessage((m) => m.type === 'blocked_url');
  expect(report).toMatchObject({ sessionId, url: offlistUrl, host: OFFLIST_HOST });
  expect(typeof report.tabId).toBe('number');
});

test('navigating to an on-list site is left untouched', async ({ ext, staticServer }) => {
  const onlistUrl = staticServer.url(BASELINE_ONLIST_HOST, '/allowed');
  const page = await ext.context.newPage();
  await page.goto(onlistUrl);

  // Give any (erroneous) redirect a beat to happen, then assert it didn't.
  await expect(page.locator('#ok')).toHaveText('anchor-e2e');
  expect(page.url()).toContain(BASELINE_ONLIST_HOST);
  expect(page.url()).not.toContain('block-page.html');
});

test('navigating to the dashboard is left untouched', async ({ ext, staticServer }) => {
  // #409: the deployment's dashboard host is on every session's allowlist,
  // whatever bundle is attached.
  const page = await ext.context.newPage();
  await page.goto(staticServer.url(DASHBOARD_HOST, '/during-session'));

  await expect(page.locator('#where')).toContainText(DASHBOARD_HOST);
  expect(page.url()).not.toContain('block-page.html');
});

test('with the agent gone, the session stays in force', async ({ ext, agent, staticServer }) => {
  // The agent crashed or was killed, and doesn't come back: the host tells the
  // extension so. Nothing says the session is over, so it keeps filtering —
  // Edge doesn't become usable unfiltered because the agent is away.
  const connections = agent.connections;
  await agent.goAway();
  await ext.waitForLog('agent witness reported unavailable');

  const page = await ext.context.newPage();
  await page.goto(staticServer.url(OFFLIST_HOST, '/agent-gone')).catch(() => {});

  await expectRedirectedToBlockPage(page, ext, OFFLIST_HOST);
  // Blocked on the session it had, not on one a reconnect handed down again.
  expect(agent.connections).toBe(connections);
});
