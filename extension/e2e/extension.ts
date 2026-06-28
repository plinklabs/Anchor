// Loads the *real* unpacked extension into Edge and gives the specs a small,
// reliable surface over it: the extension id, a live feed of service-worker
// console logs, and a configure() step that points the extension at the e2e
// backend and waits until its SignalR hub is actually connected.
//
// The MV3 service worker reads its settings (backendUrl, devImpersonateOid)
// from chrome.storage.local exactly once, at the top of background.ts, on a
// cold start. A freshly-loaded extension therefore comes up *unconfigured* and
// refuses to connect. We can't pre-seed storage before the worker first runs,
// so configure() writes the settings and then calls chrome.runtime.reload()
// to force a cold restart that re-reads them — verified to re-run background.ts
// top-level and re-establish the hub.

import { chromium, type BrowserContext, type Worker } from '@playwright/test';
import os from 'node:os';
import path from 'node:path';
import fs from 'node:fs';
import {
  BACKEND_URL,
  BROWSER_CHANNEL,
  DIST_PATH,
  HEADLESS,
  MAPPED_HOSTS,
  STUDENT_OID,
} from './config.ts';
import { registerWitnessHost, type RegisteredWitnessHost } from './witness-host.ts';

export interface LoadExtensionOptions {
  /**
   * When set (Windows only), register the REAL witness native-messaging host
   * and launch Edge with this URL in ANCHOR_WITNESS_BACKEND_URL, so the host
   * hands it to the extension over native messaging at runtime (#204). Lets a
   * spec prove the extension learns its backend from the on-box agent rather
   * than a seeded chrome.storage value.
   */
  witnessBackendUrl?: string;
  /**
   * When set (Windows only), launch Edge with the ANCHOR_WITNESS_AUTH_* env vars
   * so the real host also hands down a production auth_config (#289). Lets a spec
   * prove the agent→extension auth-config plumbing works end-to-end (the AAD
   * sign-in itself can't be driven without a real tenant, so it's out of scope).
   */
  witnessAuth?: { tenantId: string; clientId: string; scope: string };
}

export interface ExtensionSettings {
  backendUrl?: string;
  /** Seeded student OID to impersonate over the hub's query-string auth. */
  devImpersonateOid?: string;
}

export interface LoadedExtension {
  readonly context: BrowserContext;
  readonly extensionId: string;
  /** chrome-extension://<id>/block-page.html — prefix of every block URL. */
  readonly blockPagePrefix: string;
  /** Every service-worker / page console line seen since launch. */
  readonly logs: readonly string[];
  /** Resolve once a console line containing `substring` has been observed
   *  (checks already-seen lines first), else reject after `timeout` ms. */
  waitForLog(substring: string, timeout?: number): Promise<string>;
  /** Write settings, cold-restart the SW, and wait for the hub to connect.
   *  Returns the post-restart service worker. */
  configure(settings?: ExtensionSettings): Promise<Worker>;
  close(): Promise<void>;
}

export async function loadExtension(options: LoadExtensionOptions = {}): Promise<LoadedExtension> {
  if (!fs.existsSync(path.join(DIST_PATH, 'manifest.json'))) {
    throw new Error(`No built extension at ${DIST_PATH}. Run \`npm run build\` first.`);
  }

  // #204: optionally register the real witness host and inject the backend URL
  // it should hand the extension. connectNative inherits the browser's env, so
  // setting it here is what the launched host reads.
  let witnessHost: RegisteredWitnessHost | null = null;
  if (options.witnessBackendUrl || options.witnessAuth) {
    witnessHost = registerWitnessHost();
  }
  if (options.witnessBackendUrl) {
    process.env.ANCHOR_WITNESS_BACKEND_URL = options.witnessBackendUrl;
  }
  // #289: the host reads these from its inherited env and hands the values down
  // as an auth_config message (the per-deployment source).
  if (options.witnessAuth) {
    process.env.ANCHOR_WITNESS_AUTH_TENANT_ID = options.witnessAuth.tenantId;
    process.env.ANCHOR_WITNESS_AUTH_CLIENT_ID = options.witnessAuth.clientId;
    process.env.ANCHOR_WITNESS_AUTH_SCOPE = options.witnessAuth.scope;
  }

  const userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'anchor-ext-e2e-'));
  const hostRule = MAPPED_HOSTS.map((h) => `MAP ${h} 127.0.0.1`).join(',');
  const context = await chromium.launchPersistentContext(userDataDir, {
    channel: BROWSER_CHANNEL,
    headless: HEADLESS,
    args: [
      `--disable-extensions-except=${DIST_PATH}`,
      `--load-extension=${DIST_PATH}`,
      // Resolve the synthetic test hosts to the local static server so specs
      // never hit the public internet (config.MAPPED_HOSTS).
      `--host-resolver-rules=${hostRule}`,
    ],
  });

  const { logs, waitForLog } = attachConsoleFeed(context);

  const firstWorker = await getServiceWorker(context);
  const extensionId = new URL(firstWorker.url()).host;
  const blockPagePrefix = `chrome-extension://${extensionId}/block-page.html`;

  async function configure(settings: ExtensionSettings = {}): Promise<Worker> {
    const backendUrl = settings.backendUrl ?? BACKEND_URL;
    const devImpersonateOid = settings.devImpersonateOid ?? STUDENT_OID;

    const current = await getServiceWorker(context);
    await current.evaluate(
      (s) => chrome.storage.local.set(s),
      { backendUrl, devImpersonateOid },
    );

    // chrome.runtime.reload() tears down this worker and starts a fresh one
    // that re-reads storage. Race-proof the swap by arming the waiter before
    // firing the reload; the reload() evaluate rejects as the context dies, so
    // swallow it.
    const nextWorker = context.waitForEvent('serviceworker', {
      predicate: (w) => w !== current,
      timeout: 15_000,
    });
    await current.evaluate(() => chrome.runtime.reload()).catch(() => {});
    const worker = await nextWorker;

    await waitForLog('hub connection established', 20_000);
    return worker;
  }

  return {
    context,
    extensionId,
    blockPagePrefix,
    logs,
    waitForLog,
    configure,
    async close() {
      await context.close();
      if (witnessHost) {
        witnessHost.unregister();
        delete process.env.ANCHOR_WITNESS_BACKEND_URL;
        delete process.env.ANCHOR_WITNESS_AUTH_TENANT_ID;
        delete process.env.ANCHOR_WITNESS_AUTH_CLIENT_ID;
        delete process.env.ANCHOR_WITNESS_AUTH_SCOPE;
      }
      fs.rmSync(userDataDir, { recursive: true, force: true });
    },
  };
}

/** First service worker, waiting for it to register if it hasn't yet. */
async function getServiceWorker(context: BrowserContext): Promise<Worker> {
  return context.serviceWorkers()[0] ?? (await context.waitForEvent('serviceworker'));
}

/** Collect every console line from the context (pages + service workers) and
 *  expose a substring waiter over the running buffer. */
function attachConsoleFeed(context: BrowserContext): {
  logs: string[];
  waitForLog: (substring: string, timeout?: number) => Promise<string>;
} {
  const logs: string[] = [];
  const waiters: Array<{ substring: string; resolve: (line: string) => void }> = [];

  context.on('console', (msg) => {
    const text = msg.text();
    logs.push(text);
    for (let i = waiters.length - 1; i >= 0; i--) {
      if (text.includes(waiters[i].substring)) {
        waiters[i].resolve(text);
        waiters.splice(i, 1);
      }
    }
  });

  function waitForLog(substring: string, timeout = 15_000): Promise<string> {
    const seen = logs.find((line) => line.includes(substring));
    if (seen) return Promise.resolve(seen);

    return new Promise<string>((resolve, reject) => {
      const waiter = { substring, resolve };
      waiters.push(waiter);
      setTimeout(() => {
        const idx = waiters.indexOf(waiter);
        if (idx < 0) return; // already resolved
        waiters.splice(idx, 1);
        reject(
          new Error(
            `Timed out after ${timeout}ms waiting for a console line containing ` +
              `"${substring}".\n--- console so far ---\n${logs.join('\n')}`,
          ),
        );
      }, timeout);
    });
  }

  return { logs, waitForLog };
}
