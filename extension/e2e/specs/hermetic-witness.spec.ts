// #332: every spec must be hermetic — no on-box agent but the one the spec
// brings may reach the extension under test.
//
// On a developer machine the installed Anchor agent registers the witness
// native-messaging host permanently (HKCU\…\Edge\NativeMessagingHosts\
// net.anchor.witness). Since #489 that link is where the extension gets its
// session from, so a spec that let the installed host through would enforce
// whatever session that box's real student is in, over whatever the spec
// drives. CI never sees it (no agent on the runner), which is the worst shape a
// harness bug can take: "the e2e suite doesn't pass on my machine".
//
// This drives that exact situation deliberately, so it reproduces everywhere:
// stand up an "installed" agent of our own, in a session, and register the REAL
// host for it the way the agent does — then load the extension the way the
// specs do and prove the installed agent never hears from it. It also proves the
// harness leaves the registry the way it found it, which is what keeps a
// developer's agent link working after a run.
//
// The stand-in allows EXTENSION_ID, the id of the extension under test, so the
// link would work if the harness didn't cut it. That is the pinned id, except on
// a box whose Edge policy force-installs Anchor, where it is the key-less copy's
// path-derived id (#429).

import { test, expect } from '../fixtures.ts';
import { WITH_MS365 } from '../config.ts';
import { EXTENSION_ID, loadExtension, type LoadedExtension } from '../extension.ts';
import { FakeAgent } from '../fake-agent.ts';
import {
  readWitnessHostRegistration,
  registerWitnessHost,
  witnessHostSupported,
} from '../witness-host.ts';

/** Either line the extension logs when connectNative finds no host: the port is
 *  dropped before any message (a manifest that isn't there, #243) or the connect
 *  throws outright. Waiting for one of them makes "nothing got through" a
 *  positive assertion about a *dead* link rather than a sleep. */
const DEAD_LINK_LINES = [
  'witness port dropped before any host message',
  'connectNative failed',
];

async function waitForDeadWitnessLink(ext: LoadedExtension, timeout = 20_000): Promise<void> {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    if (DEAD_LINK_LINES.some((line) => ext.countLogs(line) > 0)) return;
    await new Promise((r) => setTimeout(r, 200));
  }
  throw new Error(
    `The witness link never reported itself dead within ${timeout}ms — the host ` +
      `was still reachable from the extension under test.\n--- console so far ---\n` +
      ext.logs.join('\n'),
  );
}

/** Stand in for the developer box: an agent in a session, with the real host
 *  registered for it in HKCU. */
async function installAgent(): Promise<{ agent: FakeAgent; manifestPath: string; uninstall(): Promise<void> }> {
  const agent = await FakeAgent.start();
  agent.startSession(WITH_MS365, { send: false });
  process.env.ANCHOR_WITNESS_PIPE = agent.pipeName;
  const registration = registerWitnessHost(EXTENSION_ID);
  return {
    agent,
    manifestPath: registration.manifestPath,
    async uninstall() {
      registration.unregister();
      delete process.env.ANCHOR_WITNESS_PIPE;
      await agent.close();
    },
  };
}

test.describe('the suite is hermetic against an installed agent (#332)', () => {
  test.skip(!witnessHostSupported(), 'native witness host registration is Windows-only');

  test('an installed agent cannot reach a spec that brings no agent of its own', async () => {
    const beforeRun = readWitnessHostRegistration();
    const installed = await installAgent();
    try {
      expect(readWitnessHostRegistration()).toBe(installed.manifestPath);

      const ext = await loadExtension();
      try {
        await waitForDeadWitnessLink(ext);
        expect(installed.agent.connections).toBe(0);
        expect(ext.countLogs('active session cached')).toBe(0);
      } finally {
        await ext.close();
      }

      // Closing the extension gives the box its agent link back rather than
      // leaving it pointed at the harness's missing manifest.
      expect(readWitnessHostRegistration()).toBe(installed.manifestPath);
    } finally {
      await installed.uninstall();
    }
    expect(readWitnessHostRegistration()).toBe(beforeRun);
  });

  test("a spec's own agent is the only one the extension talks to", async () => {
    const beforeRun = readWitnessHostRegistration();
    const installed = await installAgent();
    const own = await FakeAgent.start();
    try {
      const ext = await loadExtension({ agent: own });
      try {
        await own.waitForConnection();
        await ext.waitForLog('agent message: no_session');
        expect(installed.agent.connections).toBe(0);
        expect(ext.countLogs('active session cached')).toBe(0);
      } finally {
        await ext.close();
      }

      expect(readWitnessHostRegistration()).toBe(installed.manifestPath);
      // The spec's pipe doesn't outlive it: the installed agent's is back.
      expect(process.env.ANCHOR_WITNESS_PIPE).toBe(installed.agent.pipeName);
    } finally {
      await own.close();
      await installed.uninstall();
    }
    expect(readWitnessHostRegistration()).toBe(beforeRun);
  });
});
