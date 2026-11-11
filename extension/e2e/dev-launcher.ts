// One-command dev loop for the Edge extension (#124 sequencing step 2).
//
//   npm run dev:extension
//
// Opens Edge with the freshly-built unpacked extension connected, through the
// REAL native-messaging host, to a stand-in for the on-box agent — the only
// place the extension gets a session from (#489) — and drops you into a tiny
// console to start / change / end a session as the agent would hand it down.
// No backend, no dashboard, no agent, no edge://extensions clicking.
//
// Windows-only, like the native host. Run plain: `node e2e/dev-launcher.ts`
// (Node strips the TS types).

import { chromium, type BrowserContext } from '@playwright/test';
import readline from 'node:readline';
import os from 'node:os';
import path from 'node:path';
import fs from 'node:fs';
import { BASELINE, DIST_PATH, STABLE_EXTENSION_ID, WITH_MS365 } from './config.ts';
import { describeEntry, extensionIdForPath, prepareEdgeExtension } from './edge-forcelist.ts';
import { FakeAgent } from './fake-agent.ts';
import { registerWitnessHost, witnessHostSupported, type RegisteredWitnessHost } from './witness-host.ts';

function log(msg: string): void {
  console.log(`\x1b[36m[dev]\x1b[0m ${msg}`);
}

async function launchEdge(agent: FakeAgent): Promise<{ context: BrowserContext; host: RegisteredWitnessHost }> {
  if (!fs.existsSync(path.join(DIST_PATH, 'manifest.json'))) {
    throw new Error(`No built extension at ${DIST_PATH}. Run \`npm run build\` first.`);
  }
  // On a box whose Edge policy force-installs Anchor (the agent's HKLM entry,
  // #405), Edge refuses dist/, which claims the pinned id, and the launch never
  // returns. Load a key-less copy there instead, as the e2e harness does (#429,
  // #431), and register the host for the copy's id.
  const extension = prepareEdgeExtension(
    DIST_PATH,
    STABLE_EXTENSION_ID,
    path.join(os.tmpdir(), `anchor-ext-dev-keyless-${process.pid}`),
  );
  const extensionId = extension.forcelistEntry ? extensionIdForPath(extension.path) : STABLE_EXTENSION_ID;
  if (extension.forcelistEntry) {
    log(
      `Running a key-less copy of dist/ as ${extensionId} (${extension.path}): Edge policy ` +
        `force-installs Anchor (${describeEntry(extension.forcelistEntry)}) and refuses dist/, ` +
        `which claims ${STABLE_EXTENSION_ID}.`,
    );
  }

  // The host Edge starts inherits this environment, so it connects to the fake
  // agent rather than the installed one. The registry key is put back on exit.
  const host = registerWitnessHost(extensionId);
  process.env.ANCHOR_WITNESS_PIPE = agent.pipeName;

  const userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'anchor-ext-dev-'));
  const context = await chromium.launchPersistentContext(userDataDir, {
    channel: 'msedge',
    headless: false,
    args: [`--disable-extensions-except=${extension.path}`, `--load-extension=${extension.path}`],
  });
  await agent.waitForConnection();
  log('Extension loaded and connected to the stand-in agent.');
  return { context, host };
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
      '  │  q  quit (closes Edge)                                     │',
      '  └────────────────────────────────────────────────────────────┘',
      '  Try it: with a session running, open https://reddit.com → blocked;',
      '  open https://team.sharepoint.com → allowed.',
      '',
    ].join('\n'),
  );
}

async function main(): Promise<void> {
  if (!witnessHostSupported()) throw new Error('The dev loop needs Windows: the native host is Windows-only.');
  const agent = await FakeAgent.start();
  const { context, host } = await launchEdge(agent);
  const rl = readline.createInterface({ input: process.stdin, output: process.stdout });

  const shutdown = async () => {
    rl.close();
    await context.close().catch(() => {});
    host.unregister();
    await agent.close();
    process.exit(0);
  };

  menu();
  rl.on('line', async (raw) => {
    const cmd = raw.trim().toLowerCase();
    try {
      switch (cmd) {
        case 's':
          log(`Session started: ${agent.startSession(WITH_MS365)}.`);
          break;
        case 'a':
          agent.replaceAllowlist(BASELINE);
          log('Dropped all bundles — open tabs should re-scan and block.');
          break;
        case 'm':
          agent.replaceAllowlist(WITH_MS365);
          log('Restored the Microsoft 365 bundle.');
          break;
        case 'e':
          agent.endSession();
          log('Session ended.');
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
