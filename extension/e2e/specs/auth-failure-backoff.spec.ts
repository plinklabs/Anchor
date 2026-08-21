// #331: a student sign-in that can never succeed must not turn into a popup loop.
//
// The production failure was an app registration without implicit access-token
// issuance, so Entra answered every authorize request with AADSTS700051. The
// extension's interactive-sign-in guard lived in MV3 service-worker memory, so it
// reset on every worker revival — and a failing hub connect is itself what wakes
// the worker. The student got a sign-in window that reopened seconds after being
// closed, forever, with nothing anywhere naming the cause.
//
// This drives the real extension against a sign-in authority that cannot answer
// (login.microsoftonline.com is mapped to loopback for the whole suite, see
// config.ENTRA_AUTHORITY_HOST), which from the extension's point of view is the
// same shape of hard failure: the flow rejects, in production and here alike. It
// then hibernates and revives the worker the way Chrome does on its own, and
// asserts the guard held across that boundary — the exact step that regressed.
//
// The AADSTS parsing and the backoff ladder itself are unit-tested in
// src/shared/auth-gate.test.ts; what only a real browser can show is that the
// state survives a worker generation, that the toolbar reports it, and that the
// popup names it.
//
// Loads its own extension (rather than the `ext` fixture) to cut the witness
// link: on a developer box the installed agent's host would otherwise push that
// machine's production auth config over the one seeded here.

import { test, expect } from '../fixtures.ts';
import { BACKEND_URL } from '../config.ts';
import { loadExtension, type LoadedExtension } from '../extension.ts';

/** A syntactically valid auth config whose authority can never answer. */
const DEAD_AUTH_CONFIG = {
  tenantId: '8ee90830-e251-45a0-bf95-abdf72738b07',
  clientId: '110ab4c0-41b5-4f0e-9c2f-0d3a1b2c3d4e',
  scope: 'api://110ab4c0-41b5-4f0e-9c2f-0d3a1b2c3d4e/access_as_user',
};

const SIGN_IN_FAILED = 'student sign-in failed';
const SUPPRESSED = 'interactive sign-in suppressed';

interface StoredGate {
  authGate?: {
    attempts: number;
    nextAttemptAt: number;
    failure?: { code: string | null; message: string };
  };
}

function popupUrl(extensionId: string): string {
  return `chrome-extension://${extensionId}/popup.html`;
}

/** Put the extension into production token mode: a real auth config and no dev
 *  impersonation OID to short-circuit it. Cold-restarts the worker so
 *  background.ts re-reads settings, exactly as configure() does. */
async function configureTokenAuth(ext: LoadedExtension): Promise<void> {
  const sw = ext.context.serviceWorkers()[0]!;
  await sw.evaluate((s) => chrome.storage.local.set(s), {
    backendUrl: BACKEND_URL,
    authConfig: DEAD_AUTH_CONFIG,
  });
  const nextWorker = ext.context.waitForEvent('serviceworker', {
    predicate: (w) => w !== sw,
    timeout: 15_000,
  });
  await sw.evaluate(() => chrome.runtime.reload()).catch(() => {});
  await nextWorker;
}

/** Read the toolbar badge, retrying past an MV3 idle-restart (#313). */
async function badgeText(ext: LoadedExtension): Promise<string> {
  for (let attempt = 0; attempt < 3; attempt++) {
    const sw = ext.context.serviceWorkers()[0] ?? (await ext.context.waitForEvent('serviceworker'));
    try {
      return await sw.evaluate(() => chrome.action.getBadgeText({}));
    } catch (err) {
      if (err instanceof Error && err.message.includes('Service worker restarted')) continue;
      throw err;
    }
  }
  throw new Error('could not read the toolbar badge');
}

test('a sign-in that can never succeed is reported once and never re-pops (#331)', async () => {
  const ext = await loadExtension({ suppressWitnessHost: true });
  try {
    await configureTokenAuth(ext);

    // 1. The first connect is allowed its one sign-in attempt — and when that
    //    fails, the failure is named rather than swallowed into a dead hub.
    await ext.waitForLog(SIGN_IN_FAILED, 30_000);

    const afterFirst = await ext.getSessionStorage<StoredGate>('authGate');
    expect(afterFirst.authGate?.attempts).toBe(1);
    expect(afterFirst.authGate?.nextAttemptAt).toBeGreaterThan(Date.now());
    expect(afterFirst.authGate?.failure?.message).toBeTruthy();

    // 2. The toolbar action carries the failure, so the student sees that
    //    something is wrong without having to open anything.
    expect(await badgeText(ext)).toBe('!');

    // 3. The popup names the cause, in text the student can relay.
    const page = await ext.context.newPage();
    await page.goto(popupUrl(ext.extensionId));
    const notice = page.locator('[data-auth-error]');
    await expect(notice).toBeVisible();
    const detail = notice.locator('[data-auth-error-detail]');
    await expect(detail).not.toBeEmpty();
    await expect(detail).not.toHaveText('—');
    await page.close();

    // 4. The regression itself: hibernate the worker and let a browsing event
    //    revive it. Pre-#331 the guard was module state, so this generation
    //    would happily open a second sign-in window — the loop. The attempt
    //    counter must therefore still read 1, and the worker must say out loud
    //    that it stayed silent.
    const failuresBefore = ext.countLogs(SIGN_IN_FAILED);
    await ext.restartServiceWorker();
    await ext.waitForLog(SUPPRESSED, 30_000);
    // The silent flow still runs (a repaired config must recover without waiting
    // out the backoff), so the revived worker fails again — with no window.
    await ext.waitForLogCount(SIGN_IN_FAILED, failuresBefore + 1, 30_000);

    const afterRestart = await ext.getSessionStorage<StoredGate>('authGate');
    expect(afterRestart.authGate?.attempts).toBe(1);
  } finally {
    await ext.close();
  }
});
