// #332: a spec that seeds its own settings must be hermetic — no on-box agent
// may push config into it.
//
// On a developer machine the installed Anchor agent registers the witness
// native-messaging host permanently (HKCU\…\Edge\NativeMessagingHosts\
// net.anchor.witness). Every spec used to launch Edge with that key in place, so
// the extension under test connected to the REAL installed host, which handed it
// that box's *production* backend URL and Entra auth config over the witness link
// (#204/#289) — straight over whatever the harness had just seeded, hub restart
// included. CI never saw it (no agent on the runner), which is the worst shape a
// harness bug can take: "the e2e suite doesn't pass on my machine".
//
// This drives that exact situation deliberately, so it reproduces everywhere:
// register the REAL host ourselves — standing in for the developer's installed
// agent — and hand it a backend URL that is NOT the e2e one, then load the
// extension the way every ordinary spec does and prove none of it reached the
// extension. It also proves the harness leaves the registry the way it found it,
// which is what keeps a developer's agent link working after a run.
//
// Windows-only: native messaging + the host build/registration need Edge + .NET
// on a Windows runner, which is exactly what the Extension E2E CI job uses.

import { test, expect } from '../fixtures.ts';
import { BACKEND_URL } from '../config.ts';
import { loadExtension, type LoadedExtension } from '../extension.ts';
import {
  readWitnessHostRegistration,
  registerWitnessHost,
  witnessHostSupported,
} from '../witness-host.ts';

/** What the stand-in "installed agent" hands down: deliberately not the e2e
 *  backend, so a push is unmistakable in storage and in the logs. */
const AGENT_BACKEND_URL = 'http://agent-pushed.test:9';

/** The witness line the extension logs when the host is handing config down —
 *  the exact symptom #332 is about (WitnessClient.handleMessage). */
const HANDED_DOWN = 'agent handed down backend url';

/** Either line the extension logs when connectNative finds no host: the port is
 *  dropped before any message (a manifest that isn't there, #243) or the connect
 *  throws outright. Waiting for one of them makes "nothing was pushed" a
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

test.describe('the suite is hermetic against an installed agent (#332)', () => {
  test.skip(!witnessHostSupported(), 'native witness host registration is Windows-only');

  test('an on-box witness host cannot push its config into an ordinary spec', async () => {
    const beforeRun = readWitnessHostRegistration();

    // Stand in for the developer box: the real host registered in HKCU, with the
    // backend URL it should hand down in the environment the browser inherits.
    process.env.ANCHOR_WITNESS_BACKEND_URL = AGENT_BACKEND_URL;
    const installed = registerWitnessHost();
    try {
      expect(readWitnessHostRegistration()).toBe(installed.manifestPath);

      // An ordinary spec: no witness options, settings seeded by the harness.
      const ext = await loadExtension();
      try {
        await ext.configure();

        // The extension found no host at all, so it never learned the agent's
        // backend URL — and the seeded one is still what the hub runs on.
        await waitForDeadWitnessLink(ext);
        expect(ext.countLogs(HANDED_DOWN)).toBe(0);
        const stored = await ext.getStorage('backendUrl');
        expect(stored.backendUrl).toBe(BACKEND_URL);
      } finally {
        await ext.close();
      }

      // Closing the extension gives the box its agent link back rather than
      // leaving it pointed at the harness's missing manifest.
      expect(readWitnessHostRegistration()).toBe(installed.manifestPath);
    } finally {
      installed.unregister();
      delete process.env.ANCHOR_WITNESS_BACKEND_URL;
    }

    // And the witness specs' own registration restores what it found too, so a
    // run never unregisters a developer's installed agent from Edge.
    expect(readWitnessHostRegistration()).toBe(beforeRun);
  });
});
