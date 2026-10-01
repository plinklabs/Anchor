// #374 — sessions still reach the extension after an outage longer than
// SignalR's default reconnect window.
//
// The hub client used SignalR's default reconnect policy, which retries at 0, 2,
// 10 and 30 s and then closes the connection for good, and nothing started a
// closed connection again, nor one whose first start failed. In production the
// witness port keeps the service worker, and with it that dead client, alive.
// So after about 42 s offline (a backend restart, a dropped network, a sleeping
// laptop) the extension never heard SessionStarted or SessionEnded again until
// the browser restarted: a session that started was never enforced, and a
// student stayed blocked after the session ended.
//
// A relay between the extension and the backend cuts the network while the
// teacher's side (BackendClient) carries on. Each test checks that the service
// worker that lost the connection is the one that got it back: a revived worker
// opens a new connection anyway, which would hide the bug.

import { test as base, expect } from '../fixtures.ts';
import { expectRedirectedToBlockPage } from '../asserts.ts';
import { OFFLIST_HOST } from '../config.ts';
import { startNetworkRelay, type NetworkRelay } from '../network-relay.ts';
import type { LoadedExtension } from '../extension.ts';
import type { Page } from '@playwright/test';

const test = base.extend<{ relay: NetworkRelay }>({
  relay: async ({}, use) => {
    const relay = await startNetworkRelay();
    await use(relay);
    await relay.close();
  },
});

/** How long the network stays down: past the ~42 s after which SignalR's
 *  default policy closed the connection for good. */
const LONG_OUTAGE_MS = 50_000;
/** Where in the outage the teacher acts: after that give-up point. */
const TEACHER_ACTS_AT_MS = 44_000;
/** Once the network is back, the next attempt is at most 30 s away (#374's
 *  backoff cap), then it connects and catches up. */
const BACK_WITHIN_MS = 45_000;

const WORKER_STARTED = 'service worker started';

test.describe.configure({ timeout: 180_000 });

test('a session that starts during a long outage blocks the open off-list tab once the network is back, and ending it afterwards restores the tab', async ({
  ext,
  backend,
  staticServer,
  relay,
}) => {
  await ext.configure({ backendUrl: relay.url });
  const workers = ext.countLogs(WORKER_STARTED);

  const page = await ext.context.newPage();
  await page.goto(staticServer.url(OFFLIST_HOST, '/before-session'));
  const classId = await backend.findClassId();
  const bundleId = await backend.findBundleId();

  const session = await outage(relay, () => backend.startSession(classId, [bundleId]));
  // Nothing reached the extension while it was cut off.
  expect(page.url()).not.toContain('block-page.html');

  await expectRedirectedToBlockPage(page, ext, OFFLIST_HOST, BACK_WITHIN_MS);

  // Ended once the extension is back: the SessionEnded broadcast reaches it.
  await backend.endSession(session.id);
  await expectRestored(page, ext);

  expect(ext.countLogs(WORKER_STARTED)).toBe(workers);
});

test('a session that ends during a long outage restores the blocked tab once the network is back, and a session started afterwards blocks it again', async ({
  ext,
  backend,
  staticServer,
  relay,
}) => {
  await ext.configure({ backendUrl: relay.url });
  const workers = ext.countLogs(WORKER_STARTED);

  const page = await ext.context.newPage();
  await page.goto(staticServer.url(OFFLIST_HOST, '/before-session'));
  const classId = await backend.findClassId();
  const bundleId = await backend.findBundleId();
  const first = await backend.startSession(classId, [bundleId]);
  await expectRedirectedToBlockPage(page, ext, OFFLIST_HOST);

  await outage(relay, () => backend.endSession(first.id));
  // Nothing reached the extension while it was cut off.
  expect(page.url()).toContain('block-page.html');

  await expectRestored(page, ext, BACK_WITHIN_MS);

  // Started once the extension is back: the SessionStarted broadcast reaches it.
  await backend.startSession(classId, [bundleId]);
  await expectRedirectedToBlockPage(page, ext, OFFLIST_HOST);

  expect(ext.countLogs(WORKER_STARTED)).toBe(workers);
});

test('a session that starts while the network was down as the extension started blocks the open off-list tab once the network is up', async ({
  ext,
  backend,
  staticServer,
  relay,
}) => {
  // The browser starts before the network does: the first connect fails.
  relay.cut();
  await ext.configure({ backendUrl: relay.url }, { awaitHub: false });
  await ext.waitForLog('hub start failed', 20_000);
  const workers = ext.countLogs(WORKER_STARTED);

  const page = await ext.context.newPage();
  await page.goto(staticServer.url(OFFLIST_HOST, '/before-session'));
  const classId = await backend.findClassId();
  const bundleId = await backend.findBundleId();
  await backend.startSession(classId, [bundleId]);
  expect(page.url()).not.toContain('block-page.html');

  relay.restore();
  await expectRedirectedToBlockPage(page, ext, OFFLIST_HOST, BACK_WITHIN_MS);

  expect(ext.countLogs(WORKER_STARTED)).toBe(workers);
});

/**
 * Cuts the network for LONG_OUTAGE_MS, has the teacher act after SignalR's old
 * give-up point, and brings the network back. Returns what the teacher's action
 * returned.
 */
async function outage<T>(relay: NetworkRelay, teacherActs: () => Promise<T>): Promise<T> {
  const cutAt = Date.now();
  relay.cut();
  await sleep(TEACHER_ACTS_AT_MS);
  const result = await teacherActs();
  await sleep(LONG_OUTAGE_MS - (Date.now() - cutAt));
  relay.restore();
  return result;
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, Math.max(0, ms)));
}

async function expectRestored(page: Page, ext: LoadedExtension, timeout = 15_000): Promise<void> {
  // Poll on *leaving* the block page: its URL embeds the off-list host too.
  await expect.poll(() => page.url(), { timeout }).not.toContain('block-page.html');
  expect(page.url()).toContain(OFFLIST_HOST);
  expect(page.url()).toContain('/before-session');
  const { activeSession } = await ext.getSessionStorage<{ activeSession?: unknown }>('activeSession');
  expect(activeSession).toBeUndefined();
}
