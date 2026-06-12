// One-command dev loop for the Edge extension (#124 sequencing step 2).
//
//   npm run dev:extension
//
// Boots a seeded backend if one isn't already running, opens Edge with the
// freshly-built unpacked extension preconfigured to impersonate the seeded Dev
// Student, and drops you into a tiny REST console to start / amend / end a
// session. No dashboard, no agent, no edge://extensions clicking.
//
// Run plain: `node e2e/dev-launcher.ts` (Node strips the TS types).

import { chromium, type BrowserContext, type Worker } from '@playwright/test';
import { spawn, type ChildProcess } from 'node:child_process';
import readline from 'node:readline';
import os from 'node:os';
import path from 'node:path';
import fs from 'node:fs';
import { BackendClient } from './backend.ts';
import { BACKEND_PROJECT, CLASS_NAME, DIST_PATH, MS365_BUNDLE_NAME, STUDENT_OID } from './config.ts';

// Dev port / DB, deliberately the project defaults so this matches what the
// dashboard and agent talk to — reuse a running dev backend if there is one.
const DEV_BACKEND_URL = process.env.ANCHOR_DEV_BACKEND ?? 'http://localhost:5276';
const DEV_BACKEND_PORT = Number(new URL(DEV_BACKEND_URL).port || 5276);

function log(msg: string): void {
  console.log(`\x1b[36m[dev]\x1b[0m ${msg}`);
}

async function isBackendUp(): Promise<boolean> {
  try {
    // Any HTTP response (even 401/404) means it's listening.
    await fetch(DEV_BACKEND_URL, { signal: AbortSignal.timeout(2000) });
    return true;
  } catch {
    return false;
  }
}

async function bootBackend(): Promise<ChildProcess> {
  log(`Starting backend at ${DEV_BACKEND_URL} (Development, seeded)…`);
  const child = spawn(
    'dotnet',
    ['run', '--project', BACKEND_PROJECT, '--no-launch-profile', '--urls', DEV_BACKEND_URL],
    {
      stdio: 'inherit',
      shell: process.platform === 'win32',
      env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development' },
    },
  );
  const deadline = Date.now() + 180_000;
  while (Date.now() < deadline) {
    await new Promise((r) => setTimeout(r, 1500));
    if (await isBackendUp()) return child;
  }
  throw new Error('Backend did not become reachable within 180s.');
}

async function launchEdge(): Promise<{ context: BrowserContext; serviceWorker: Worker }> {
  if (!fs.existsSync(path.join(DIST_PATH, 'manifest.json'))) {
    throw new Error(`No built extension at ${DIST_PATH}. Run \`npm run build\` first.`);
  }
  const userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'anchor-ext-dev-'));
  const context = await chromium.launchPersistentContext(userDataDir, {
    channel: 'msedge',
    headless: false,
    args: [`--disable-extensions-except=${DIST_PATH}`, `--load-extension=${DIST_PATH}`],
  });

  let sw = context.serviceWorkers()[0] ?? (await context.waitForEvent('serviceworker'));
  await sw.evaluate(
    (s) => chrome.storage.local.set(s),
    { backendUrl: DEV_BACKEND_URL, devImpersonateOid: STUDENT_OID },
  );
  const nextWorker = context.waitForEvent('serviceworker', { predicate: (w) => w !== sw, timeout: 15_000 });
  await sw.evaluate(() => chrome.runtime.reload()).catch(() => {});
  sw = await nextWorker;
  log('Extension loaded and configured to impersonate the seeded Dev Student.');
  return { context, serviceWorker: sw };
}

function menu(): void {
  console.log(
    [
      '',
      '  ┌─ Anchor extension dev console ─────────────────────────────┐',
      '  │  s  start a session (Microsoft 365 bundle)                 │',
      '  │  a  amend → drop all bundles (turns open tabs off-list)    │',
      '  │  m  amend → restore the Microsoft 365 bundle               │',
      '  │  e  end the current session                                │',
      '  │  q  quit (closes Edge, stops backend if we started it)     │',
      '  └────────────────────────────────────────────────────────────┘',
      '  Try it: with a session running, open https://reddit.com → blocked;',
      '  open https://outlook.office.com → allowed.',
      '',
    ].join('\n'),
  );
}

async function main(): Promise<void> {
  const backend = new BackendClient(DEV_BACKEND_URL);

  let backendChild: ChildProcess | null = null;
  if (await isBackendUp()) {
    log(`Reusing backend already running at ${DEV_BACKEND_URL}.`);
  } else {
    backendChild = await bootBackend();
  }

  const classId = await backend.findClassId(CLASS_NAME);
  const bundleId = await backend.findBundleId(MS365_BUNDLE_NAME);
  log(`Resolved class '${CLASS_NAME}' and bundle '${MS365_BUNDLE_NAME}'.`);

  const { context } = await launchEdge();

  let sessionId: string | null = null;
  const rl = readline.createInterface({ input: process.stdin, output: process.stdout });

  const shutdown = async () => {
    rl.close();
    await context.close().catch(() => {});
    if (backendChild && !backendChild.killed) {
      log('Stopping backend we started…');
      backendChild.kill();
    }
    process.exit(0);
  };

  menu();
  rl.on('line', async (raw) => {
    const cmd = raw.trim().toLowerCase();
    try {
      switch (cmd) {
        case 's': {
          const s = await backend.startSession(classId, [bundleId]);
          sessionId = s.id;
          log(`Session started: ${s.id} (join code ${s.joinCode}).`);
          break;
        }
        case 'a':
          if (!sessionId) { log('No active session — press s first.'); break; }
          await backend.updateBundles(sessionId, []);
          log('Dropped all bundles — open tabs should re-scan and block.');
          break;
        case 'm':
          if (!sessionId) { log('No active session — press s first.'); break; }
          await backend.updateBundles(sessionId, [bundleId]);
          log('Restored the Microsoft 365 bundle.');
          break;
        case 'e':
          if (!sessionId) { log('No active session — press s first.'); break; }
          await backend.endSession(sessionId);
          log(`Session ${sessionId} ended.`);
          sessionId = null;
          break;
        case 'q':
          await shutdown();
          return;
        default:
          if (cmd) log(`Unknown command '${cmd}'.`);
          menu();
      }
    } catch (err) {
      log(`Error: ${err instanceof Error ? err.message : String(err)}`);
    }
  });

  rl.on('SIGINT', shutdown);
  context.on('close', shutdown);
}

main().catch((err) => {
  console.error('dev-launcher failed:', err);
  process.exit(1);
});
