// #354 — the session ending still releases the student's tabs after the
// extension's hub connection was replaced.
//
// SignalR keeps no group membership across a new connection, and the extension
// subscribes to its session's group once, on SessionStarted. SessionEnded used
// to go only to that group, so once the MV3 service worker had been torn down
// and revived — which Chrome does routinely, and every revival opens a new hub
// connection — the end of the session never reached the extension: it kept
// blocking against the ended session's allowlist until the browser restarted.
//
// Both cases are driven with a service-worker restart:
//   - the session ends after the new connection is up;
//   - the session ends while no worker runs, so no broadcast can reach the
//     extension at all, and it has to find out once it is back.

import { test, expect } from '../fixtures.ts';
import { expectRedirectedToBlockPage } from '../asserts.ts';
import { OFFLIST_HOST } from '../config.ts';
import type { BackendClient, StartedSession } from '../backend.ts';
import type { LoadedExtension } from '../extension.ts';
import type { StaticServer } from '../static-server.ts';
import type { Page } from '@playwright/test';

const HUB_CONNECTED = 'hub connection established';

test('a redirected tab is restored when the session ends after the service worker restarted', async ({
  ext,
  backend,
  staticServer,
}) => {
  const { page, session } = await startSessionWithARedirectedTab(ext, backend, staticServer);

  const connections = ext.countLogs(HUB_CONNECTED);
  await ext.restartServiceWorker();
  await ext.waitForLogCount(HUB_CONNECTED, connections + 1, 20_000);
  // The teacher ends it a moment later, not in the same instant the extension
  // connects (see #356 for that window).
  await new Promise((resolve) => setTimeout(resolve, 1_000));

  await backend.endSession(session.id);

  await expectRestored(page, ext);
});

test('a redirected tab is restored once the extension is back, when the session ended while its service worker was stopped', async ({
  ext,
  backend,
  staticServer,
}) => {
  const { page, session } = await startSessionWithARedirectedTab(ext, backend, staticServer);

  // The SessionEnded broadcast goes out while no worker, and so no hub
  // connection, exists to receive it.
  await ext.restartServiceWorker(() => backend.endSession(session.id));

  await expectRestored(page, ext);
});

async function startSessionWithARedirectedTab(
  ext: LoadedExtension,
  backend: BackendClient,
  staticServer: StaticServer,
): Promise<{ page: Page; session: StartedSession }> {
  await ext.configure();

  const page = await ext.context.newPage();
  await page.goto(staticServer.url(OFFLIST_HOST, '/before-session'));

  // A session that doesn't cover the off-list tab, so it gets redirected.
  const classId = await backend.findClassId();
  const bundleId = await backend.findBundleId();
  const session = await backend.startSession(classId, [bundleId]);
  await expectRedirectedToBlockPage(page, ext, OFFLIST_HOST);
  return { page, session };
}

async function expectRestored(page: Page, ext: LoadedExtension): Promise<void> {
  // Poll on *leaving* the block page: its URL embeds the off-list host too.
  await expect.poll(() => page.url(), { timeout: 15_000 }).not.toContain('block-page.html');
  expect(page.url()).toContain(OFFLIST_HOST);
  expect(page.url()).toContain('/before-session');
  const { activeSession } = await ext.getSessionStorage<{ activeSession?: unknown }>('activeSession');
  expect(activeSession).toBeUndefined();
}
