// #289: production student auth. The extension is deployment-agnostic — it learns
// the Entra tenant/client/scope to sign the student in from the on-box agent at
// runtime over the native-messaging witness link, exactly like the backend URL
// (#204), rather than baking it in. This drives the REAL witness host
// (anchor-witness-host.exe) registered against the pinned e2e extension id: the
// host hands the extension an auth_config via the native channel and the extension
// persists it — with NO authConfig ever seeded into chrome.storage by the harness.
//
// Scope: this proves the agent→extension auth-config plumbing end-to-end. The
// actual token acquisition (chrome.identity.launchWebAuthFlow → Entra) can't be
// driven without a real tenant + interactive browser SSO, so the hub here still
// connects via the dev impersonation OID; the token path is covered by the
// auth.ts unit tests.
//
// Windows-only: native messaging + the host build/registration need Edge + .NET
// on a Windows runner, which is exactly what the Extension E2E CI job uses.

import { test, expect } from '../fixtures.ts';
import { BACKEND_URL, STUDENT_OID } from '../config.ts';
import { loadExtension } from '../extension.ts';
import { witnessHostSupported } from '../witness-host.ts';

const AUTH = {
  tenantId: '8ee90830-e251-45a0-bf95-abdf72738b07',
  clientId: 'c9ba7c0e-763d-4a1b-9d95-894f54fb16da',
  scope: 'api://c9ba7c0e-763d-4a1b-9d95-894f54fb16da/access_as_user',
};

test.describe('auth config handed down by the agent (#289)', () => {
  test.skip(!witnessHostSupported(), 'native witness host registration is Windows-only');

  test('extension takes its Entra auth config from the witness host and stores it', async () => {
    // Register the real host and tell it to hand down both the backend URL and the
    // auth config. A fresh profile has neither seeded, so the only way authConfig
    // can appear in storage is the host pushing it.
    const ext = await loadExtension({ witnessBackendUrl: BACKEND_URL, witnessAuth: AUTH });
    try {
      // Seed only the dev impersonation OID so the hub can still come up over the
      // dev query-string auth (real AAD sign-in is out of e2e scope, see header).
      const sw0 = ext.context.serviceWorkers()[0]!;
      await sw0.evaluate((oid) => chrome.storage.local.set({ devImpersonateOid: oid }), STUDENT_OID);

      const nextWorker = ext.context.waitForEvent('serviceworker', {
        predicate: (w) => w !== sw0,
        timeout: 15_000,
      });
      await sw0.evaluate(() => chrome.runtime.reload()).catch(() => {});
      await nextWorker;

      // The host's auth_config message is what stores authConfig — a production
      // code path (witness.ts → handleAuthConfigFromAgent → persistAuthConfig).
      await expect(ext.waitForLog('stored agent-provided auth config', 30_000)).resolves.toContain(
        'stored agent-provided auth config',
      );

      // The stored config is exactly what the agent (host) handed down.
      const sw = ext.context.serviceWorkers()[0]!;
      const stored = await sw.evaluate(() => chrome.storage.local.get('authConfig'));
      expect(stored.authConfig).toEqual(AUTH);
    } finally {
      await ext.close();
    }
  });
});
