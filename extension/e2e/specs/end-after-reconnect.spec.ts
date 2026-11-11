// #354 / #489 — the session ending still releases the student's tabs when the
// extension wasn't listening as it ended.
//
// The MV3 service worker is torn down and revived routinely, and Edge restarts;
// each time, the native-messaging link to the agent is down until the worker
// is back. The agent hands the current state to every connection — the session
// the student is in, or `no_session` — so an end the extension missed while
// away still reaches it, instead of it blocking against the ended session's
// allowlist until the browser restarts.
//
// Both cases are driven with a service-worker restart:
//   - the session ends after the link is back up;
//   - the session ends while no worker runs, so nothing can tell the extension
//     at the time, and it has to find out from the agent once it is back.

import { test, expect } from '../fixtures.ts';
import { expectRedirectedToBlockPage } from '../asserts.ts';
import { OFFLIST_HOST, WITH_MS365 } from '../config.ts';
import type { FakeAgent } from '../fake-agent.ts';
import type { LoadedExtension } from '../extension.ts';
import type { StaticServer } from '../static-server.ts';
import type { Page } from '@playwright/test';

test('a redirected tab is restored when the session ends after the service worker restarted', async ({
  ext,
  agent,
  staticServer,
}) => {
  const page = await startSessionWithARedirectedTab(ext, agent, staticServer);

  const connections = agent.connections;
  await ext.restartServiceWorker();
  await agent.waitForConnection(connections + 1);

  agent.endSession();

  await expectRestored(page, ext);
});

test('a redirected tab is restored once the extension is back, when the session ended while its service worker was stopped', async ({
  ext,
  agent,
  staticServer,
}) => {
  const page = await startSessionWithARedirectedTab(ext, agent, staticServer);

  // The session ends while no worker, and so no link, exists to hear it.
  await ext.restartServiceWorker(async () => {
    agent.endSession({ send: false });
  });

  await expectRestored(page, ext);
});

async function startSessionWithARedirectedTab(
  ext: LoadedExtension,
  agent: FakeAgent,
  staticServer: StaticServer,
): Promise<Page> {
  const page = await ext.context.newPage();
  await page.goto(staticServer.url(OFFLIST_HOST, '/before-session'));

  // A session that doesn't cover the off-list tab, so it gets redirected.
  agent.startSession(WITH_MS365);
  await expectRedirectedToBlockPage(page, ext, OFFLIST_HOST);
  return page;
}

async function expectRestored(page: Page, ext: LoadedExtension): Promise<void> {
  // Poll on *leaving* the block page: its URL embeds the off-list host too.
  await expect.poll(() => page.url(), { timeout: 15_000 }).not.toContain('block-page.html');
  expect(page.url()).toContain(OFFLIST_HOST);
  expect(page.url()).toContain('/before-session');
  const { activeSession } = await ext.getSessionStorage<{ activeSession?: unknown }>('activeSession');
  expect(activeSession).toBeUndefined();
}
