// Spike (#124 sequencing step 1): prove the loader and MV3 service-worker
// access before relying on them for behavioural specs. If this fails, nothing
// below it is trustworthy.

import { test, expect } from '../fixtures.ts';
import { BACKEND_URL, DEFAULT_LOCALE, STABLE_EXTENSION_ID, STUDENT_OID } from '../config.ts';

test('extension loads in Edge and its service worker boots', async ({ ext }) => {
  // A real MV3 extension id is 32 lowercase letters.
  expect(ext.extensionId).toMatch(/^[a-p]{32}$/);
  await expect(ext.waitForLog('service worker started')).resolves.toContain('service worker started');
});

test('Edge derives the pinned stable extension id from the manifest key', async ({ ext }) => {
  // End-to-end proof of #123: a real Edge load of the unpacked dist/ assigns the
  // id the manifest `key` pins — the same id on every machine, so the school's
  // ExtensionInstallForcelist entry keeps matching. The unit test locks the
  // manifest→id derivation; this proves the browser actually does the same.
  expect(ext.extensionId).toBe(STABLE_EXTENSION_ID);
});

test('the browser runs in the pinned UI language, whatever the host language', async ({ ext }) => {
  // #364: chrome.i18n picks its catalogue from the browser UI language, which Edge
  // otherwise takes from the host's display language, so on a Dutch box every
  // spec asserting English copy failed. Check the copy and the catalogue locale
  // (@@ui_locale), not navigator.language or getUILanguage() (so not <html lang>
  // either): Playwright Test emulates those as en-US on any host, while the
  // catalogue still followed the host.
  const page = await ext.context.newPage();
  await page.goto(`${ext.blockPagePrefix}?blocked=https://example.com/`);
  await expect(page.locator('h1')).toHaveText("Let's stay on track");
  expect(await page.evaluate(() => chrome.i18n.getMessage('@@ui_locale'))).toBe(
    DEFAULT_LOCALE.replace('-', '_'),
  );
  await page.close();
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
  const stored = await ext.getStorage(['backendUrl', 'devImpersonateOid']);
  expect(stored.backendUrl).toBe(BACKEND_URL);
  expect(stored.devImpersonateOid).toBe(STUDENT_OID);

  // …and the real SignalR hub is up (configure() already awaited this; assert
  // it explicitly so the spike's intent is self-documenting).
  expect(ext.logs.some((l) => l.includes('hub connection established'))).toBe(true);
});
