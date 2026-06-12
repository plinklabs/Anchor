// Spike (#124 sequencing step 1): prove the loader and MV3 service-worker
// access before relying on them for behavioural specs. If this fails, nothing
// below it is trustworthy.

import { test, expect } from '../fixtures.ts';
import { BACKEND_URL, STUDENT_OID } from '../config.ts';

test('extension loads in Edge and its service worker boots', async ({ ext }) => {
  // A real MV3 extension id is 32 lowercase letters.
  expect(ext.extensionId).toMatch(/^[a-p]{32}$/);
  await expect(ext.waitForLog('service worker started')).resolves.toContain('service worker started');
});

test('unconfigured extension refuses to connect to the hub', async ({ ext }) => {
  // No devImpersonateOid yet → background.ts deliberately refuses rather than
  // spinning in a 401 loop. Guards against a regression that would connect
  // (and fail) anonymously.
  await expect(ext.waitForLog('no auth configured')).resolves.toContain('refusing to connect');
});

test('configure() points the SW at the backend and connects the hub', async ({ ext }) => {
  await ext.configure({ backendUrl: BACKEND_URL, devImpersonateOid: STUDENT_OID });

  // The settings round-tripped through chrome.storage.local…
  const sw = ext.context.serviceWorkers()[0]!;
  const stored = await sw.evaluate(() =>
    chrome.storage.local.get(['backendUrl', 'devImpersonateOid']),
  );
  expect(stored.backendUrl).toBe(BACKEND_URL);
  expect(stored.devImpersonateOid).toBe(STUDENT_OID);

  // …and the real SignalR hub is up (configure() already awaited this; assert
  // it explicitly so the spike's intent is self-documenting).
  expect(ext.logs.some((l) => l.includes('hub connection established'))).toBe(true);
});
