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
  OFFLIST_HOST,
  STUDENT_OID,
} from './config.ts';
import {
  registerWitnessHost,
  suppressWitnessHost,
  type RegisteredWitnessHost,
  type SuppressedWitnessHost,
} from './witness-host.ts';

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
  /**
   * BCP-47 UI language to launch the browser in (e.g. `nl`). Passed as Chromium's
   * `--lang`, which is what `chrome.i18n` selects its `_locales/<lang>` catalogue
   * from (#322). Omit for the host default (en on the CI runners).
   */
  locale?: string;
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
  /** Resolve once at least `count` console lines containing `substring` have
   *  been seen. Use instead of waitForLog when the line is expected *again*
   *  (e.g. a second worker generation), since waitForLog matches history. */
  waitForLogCount(substring: string, count: number, timeout?: number): Promise<void>;
  /** Number of console lines seen so far containing `substring`. */
  countLogs(substring: string): number;
  /** Terminate the MV3 service worker and let a browsing event revive it —
   *  the hibernate/revive cycle Chrome performs on its own between event
   *  bursts, which is where worker-memory state is lost (#331). Resolves once
   *  the new generation has run background.js top-level again. */
  restartServiceWorker(): Promise<void>;
  /** Write settings, cold-restart the SW, and wait for the hub to connect.
   *  Returns the post-restart service worker. */
  configure(settings?: ExtensionSettings): Promise<Worker>;
  /** Read chrome.storage.local restart-safe (#313): re-acquires the live
   *  service worker on every attempt and retries if the MV3 worker idle-
   *  terminates mid-`evaluate` (Playwright throws "Service worker restarted"
   *  on the stale handle). Use this for every storage read instead of capturing
   *  `serviceWorkers()[0]` and awaiting `evaluate` on it. */
  getStorage<T = Record<string, unknown>>(keys: string | string[]): Promise<T>;
  /** Same, over chrome.storage.session — the worker-restart-surviving store the
   *  active session and the sign-in gate (#331) live in. */
  getSessionStorage<T = Record<string, unknown>>(keys: string | string[]): Promise<T>;
  close(): Promise<void>;
}

export async function loadExtension(options: LoadExtensionOptions = {}): Promise<LoadedExtension> {
  if (!fs.existsSync(path.join(DIST_PATH, 'manifest.json'))) {
    throw new Error(`No built extension at ${DIST_PATH}. Run \`npm run build\` first.`);
  }

  // The native-messaging witness link is opt-IN, per run (#332). A spec that
  // asks for it gets the REAL host, registered here with the backend URL / auth
  // config it should hand down (#204/#289; connectNative inherits the browser's
  // env, so setting it here is what the launched host reads). Every other spec
  // gets the link cut, because on a developer box the installed Anchor agent
  // owns this registry key permanently and its host would push that machine's
  // *production* backend URL and auth config over the settings the spec just
  // seeded. Either way the key is restored to what the box had on close.
  let witnessHost: RegisteredWitnessHost | null = null;
  let suppressedWitness: SuppressedWitnessHost | null = null;
  if (options.witnessBackendUrl || options.witnessAuth) {
    witnessHost = registerWitnessHost();
  } else {
    suppressedWitness = suppressWitnessHost();
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
    // `--lang` sets the browser UI language chrome.i18n reads (#322); `locale`
    // keeps navigator.language/Accept-Language consistent with it.
    ...(options.locale ? { locale: options.locale } : {}),
    args: [
      `--disable-extensions-except=${DIST_PATH}`,
      `--load-extension=${DIST_PATH}`,
      // Resolve the synthetic test hosts to the local static server so specs
      // never hit the public internet (config.MAPPED_HOSTS).
      `--host-resolver-rules=${hostRule}`,
      ...(options.locale ? [`--lang=${options.locale}`] : []),
    ],
  });

  const { logs, waitForLog, waitForLogCount, countLogs } = attachConsoleFeed(context);

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

  // Chrome tears an idle MV3 worker down and revives it on the next event it
  // has a listener for; waiting for that to happen on its own would make a spec
  // both slow and timing-dependent, so we do it deliberately. CDP's
  // Target.closeTarget stops the worker (verified: background.js top-level runs
  // again afterwards, while chrome.storage.session survives), and a top-level
  // navigation is the same wake trigger a browsing student provides.
  async function restartServiceWorker(): Promise<void> {
    const before = countLogs(WORKER_START_LOG);
    const page = await context.newPage();
    try {
      const cdp = await context.newCDPSession(page);
      const { targetInfos } = await cdp.send('Target.getTargets');
      const target = targetInfos.find(
        (t) => t.type === 'service_worker' && t.url.startsWith(`chrome-extension://${extensionId}/`),
      );
      if (!target) throw new Error('no extension service-worker target to terminate');
      await cdp.send('Target.closeTarget', { targetId: target.targetId });
      await cdp.detach();
      // The host doesn't resolve, but onBeforeNavigate fires before the request
      // is made — which is all the worker needs to wake.
      await page.goto(`http://${OFFLIST_HOST}/wake`).catch(() => {});
      await waitForLogCount(WORKER_START_LOG, before + 1, 20_000);
    } finally {
      await page.close();
    }
  }

  return {
    context,
    extensionId,
    blockPagePrefix,
    logs,
    waitForLog,
    waitForLogCount,
    countLogs,
    configure,
    restartServiceWorker,
    getStorage<T = Record<string, unknown>>(keys: string | string[]): Promise<T> {
      return readStorage<T>(context, keys, 'local');
    },
    getSessionStorage<T = Record<string, unknown>>(keys: string | string[]): Promise<T> {
      return readStorage<T>(context, keys, 'session');
    },
    async close() {
      await context.close();
      if (witnessHost) {
        witnessHost.unregister();
        delete process.env.ANCHOR_WITNESS_BACKEND_URL;
        delete process.env.ANCHOR_WITNESS_AUTH_TENANT_ID;
        delete process.env.ANCHOR_WITNESS_AUTH_CLIENT_ID;
        delete process.env.ANCHOR_WITNESS_AUTH_SCOPE;
      }
      suppressedWitness?.restore();
      fs.rmSync(userDataDir, { recursive: true, force: true });
    },
  };
}

/** First service worker, waiting for it to register if it hasn't yet. */
async function getServiceWorker(context: BrowserContext): Promise<Worker> {
  return context.serviceWorkers()[0] ?? (await context.waitForEvent('serviceworker'));
}

/** Read a chrome.storage area in a way that survives an MV3 idle-restart (#313).
 *  An idle service worker can be torn down between the moment we grab its handle
 *  and the moment `evaluate` runs, at which point Playwright throws "Service
 *  worker restarted". We re-acquire the live worker on each attempt — the
 *  `evaluate` itself wakes a terminated worker — and retry a couple of times
 *  before giving up. */
async function readStorage<T = Record<string, unknown>>(
  context: BrowserContext,
  keys: string | string[],
  area: 'local' | 'session',
): Promise<T> {
  const keyList = Array.isArray(keys) ? keys : [keys];
  const MAX_ATTEMPTS = 3;
  let lastError: unknown;
  for (let attempt = 0; attempt < MAX_ATTEMPTS; attempt++) {
    const sw = await getServiceWorker(context);
    try {
      return (await sw.evaluate(
        ([a, k]) =>
          chrome.storage[a as 'local' | 'session'].get<{ [key: string]: unknown }>(k as string[]),
        [area, keyList] as [string, string[]],
      )) as T;
    } catch (err) {
      if (err instanceof Error && err.message.includes('Service worker restarted')) {
        lastError = err;
        continue;
      }
      throw err;
    }
  }
  throw lastError;
}

/** The line background.js logs at top level on every worker generation. */
const WORKER_START_LOG = 'service worker started';

/** Collect every console line from the context (pages + service workers) and
 *  expose a substring waiter over the running buffer. */
function attachConsoleFeed(context: BrowserContext): {
  logs: string[];
  waitForLog: (substring: string, timeout?: number) => Promise<string>;
  waitForLogCount: (substring: string, count: number, timeout?: number) => Promise<void>;
  countLogs: (substring: string) => number;
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

  const countLogs = (substring: string): number =>
    logs.filter((line) => line.includes(substring)).length;

  /** Wait for the *n-th* occurrence, which waitForLog can't express: it matches
   *  the history buffer, so a line already seen resolves it immediately. */
  async function waitForLogCount(substring: string, count: number, timeout = 15_000): Promise<void> {
    const deadline = Date.now() + timeout;
    while (countLogs(substring) < count) {
      if (Date.now() >= deadline) {
        throw new Error(
          `Timed out after ${timeout}ms waiting for ${count} console lines containing ` +
            `"${substring}" (saw ${countLogs(substring)}).\n--- console so far ---\n${logs.join('\n')}`,
        );
      }
      await new Promise((r) => setTimeout(r, 100));
    }
  }

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

  return { logs, waitForLog, waitForLogCount, countLogs };
}
