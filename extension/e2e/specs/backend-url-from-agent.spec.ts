// #204: the extension is backend-agnostic — it learns which backend to target
// from the on-box agent at runtime over the native-messaging witness link,
// rather than baking the URL in. This drives the REAL witness host
// (anchor-witness-host.exe) registered against the pinned e2e extension id: the
// host hands the extension a backend URL via the native channel, the extension
// stores it and connects the hub to it — with NO backendUrl ever seeded into
// chrome.storage by the harness.
//
// Windows-only: native messaging + the host build/registration need Edge + .NET
// on a Windows runner, which is exactly what the Extension E2E CI job uses.

import { test, expect } from '../fixtures.ts';
import { BACKEND_URL, STUDENT_OID } from '../config.ts';
import { loadExtension } from '../extension.ts';
import { witnessHostSupported } from '../witness-host.ts';

test.describe('backend URL handed down by the agent (#204)', () => {
  test.skip(!witnessHostSupported(), 'native witness host registration is Windows-only');

  test('extension takes its backend URL from the witness host and connects', async () => {
    // Register the real host and tell it to hand down the e2e backend URL.
    const ext = await loadExtension({ witnessBackendUrl: BACKEND_URL });
    try {
      // Cold-load the extension: seed ONLY the dev impersonation OID, never the
      // backendUrl. A fresh profile has no backendUrl, so the only way the hub
      // can come up against BACKEND_URL is the host pushing it.
      const sw0 = ext.context.serviceWorkers()[0]!;
      await sw0.evaluate((oid) => chrome.storage.local.set({ devImpersonateOid: oid }), STUDENT_OID);

      const nextWorker = ext.context.waitForEvent('serviceworker', {
        predicate: (w) => w !== sw0,
        timeout: 15_000,
      });
      await sw0.evaluate(() => chrome.runtime.reload()).catch(() => {});
      await nextWorker;

      // The host's backend_url message is what stores backendUrl, which then
      // drives the hub up. Both lines come from the production code path.
      await expect(ext.waitForLog('stored agent-provided backend url', 30_000)).resolves.toContain(
        'stored agent-provided backend url',
      );
      await expect(ext.waitForLog('hub connection established', 30_000)).resolves.toContain(
        'hub connection established',
      );

      // The stored value is exactly what the agent (host) handed down — proving
      // it was not a baked-in default.
      const stored = await ext.getStorage('backendUrl');
      expect(stored.backendUrl).toBe(BACKEND_URL);
    } finally {
      await ext.close();
    }
  });
});
