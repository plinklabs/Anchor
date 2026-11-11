// #73 / #489 — "Request access" on the block page reaches the teacher through
// the on-box agent: the extension hands the request to the agent over the
// native-messaging link, the agent sends it with its own connection, and its
// answer decides what the block page tells the student. A request that can't be
// sent says so, rather than vanishing.

import { test, expect } from '../fixtures.ts';
import { expectRedirectedToBlockPage } from '../asserts.ts';
import { OFFLIST_HOST, WITH_MS365 } from '../config.ts';
import type { FakeAgent } from '../fake-agent.ts';
import type { LoadedExtension } from '../extension.ts';
import type { StaticServer } from '../static-server.ts';
import type { Page } from '@playwright/test';

async function blockedPage(
  ext: LoadedExtension,
  agent: FakeAgent,
  staticServer: StaticServer,
): Promise<{ page: Page; sessionId: string; url: string }> {
  const sessionId = agent.startSession(WITH_MS365);
  await ext.waitForLog('active session cached');
  const url = staticServer.url(OFFLIST_HOST, '/wanted');
  const page = await ext.context.newPage();
  await page.goto(url).catch(() => {});
  await expectRedirectedToBlockPage(page, ext, OFFLIST_HOST);
  return { page, sessionId, url };
}

test('a request for access reaches the agent, and the block page says it was sent', async ({
  ext,
  agent,
  staticServer,
}) => {
  const { page, sessionId, url } = await blockedPage(ext, agent, staticServer);

  await page.locator('[data-action="request"]').click();

  const request = await agent.waitForMessage((m) => m.type === 'unblock_request');
  expect(request).toMatchObject({ sessionId, url, host: OFFLIST_HOST });
  await expect(page.locator('[data-status]')).toContainText('Requested');
});

test('a request the agent could not send says why, and can be tried again', async ({
  ext,
  agent,
  staticServer,
}) => {
  agent.unblockAnswer = { ok: false, error: 'the school network is down' };
  const { page } = await blockedPage(ext, agent, staticServer);

  await page.locator('[data-action="request"]').click();

  await expect(page.locator('[data-status]')).toContainText("Couldn't reach teacher: the school network is down");
  await expect(page.locator('[data-action="request"]')).toBeEnabled();
});

test('a request made while the agent is away says it could not be sent', async ({
  ext,
  agent,
  staticServer,
}) => {
  const { page } = await blockedPage(ext, agent, staticServer);
  // The host is still there, and tells the extension the agent behind it went.
  await agent.goAway();
  await ext.waitForLog('agent witness reported unavailable');

  await page.locator('[data-action="request"]').click();

  await expect(page.locator('[data-status]')).toContainText(
    "Couldn't reach teacher: The Anchor app is not connected.",
  );
  await expect(page.locator('[data-action="request"]')).toBeEnabled();
});
