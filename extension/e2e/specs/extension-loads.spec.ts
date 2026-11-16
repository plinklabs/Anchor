// Spike (#124 sequencing step 1): prove the loader and MV3 service-worker
// access before relying on them for behavioural specs. If this fails, nothing
// below it is trustworthy.

import { test, expect } from '../fixtures.ts';
import { DEFAULT_LOCALE, STABLE_EXTENSION_ID } from '../config.ts';
import { describeEntry } from '../edge-forcelist.ts';
import { ANCHOR_FORCELIST_ENTRY } from '../extension.ts';

test('extension loads in Edge and its service worker boots', async ({ ext }) => {
  // A real MV3 extension id is 32 lowercase letters.
  expect(ext.extensionId).toMatch(/^[a-p]{32}$/);
  await expect(ext.waitForLog('service worker started')).resolves.toContain('service worker started');
});

test.describe('the pinned extension id (#123)', () => {
  // #429: where Edge policy force-installs Anchor (the agent's HKLM forcelist
  // entry on a dev box), Edge refuses an unpacked dist/ claiming that id, so the
  // harness loads a key-less copy with a path-derived id and this can't hold.
  // CI has no such entry and runs it.
  test.skip(
    ANCHOR_FORCELIST_ENTRY !== null,
    `Edge policy force-installs Anchor on this machine ` +
      `(${ANCHOR_FORCELIST_ENTRY ? describeEntry(ANCHOR_FORCELIST_ENTRY) : ''}), so the ` +
      `harness loads dist/ without its manifest key (#429)`,
  );

  test('Edge derives the pinned stable extension id from the manifest key', async ({ ext }) => {
    // End-to-end proof of #123: a real Edge load of the unpacked dist/ assigns the
    // id the manifest `key` pins — the same id on every machine, so the school's
    // ExtensionInstallForcelist entry keeps matching. The unit test locks the
    // manifest→id derivation; this proves the browser actually does the same.
    expect(ext.extensionId).toBe(STABLE_EXTENSION_ID);
  });
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

test('the extension connects to the on-box agent, and to nothing else (#489)', async ({ ext, agent }) => {
  // The fixture connected it: the real native host reached the agent's pipe,
  // and the agent handed down its state — no session yet.
  expect(agent.connected).toBe(true);
  await ext.waitForLog('agent message: no_session');
  // Nothing signs in in the browser and nothing talks to a backend: the agent
  // is the extension's only identity.
  expect(ext.logs.some((l) => /sign-in|hub connection|access token/i.test(l))).toBe(false);
  // The host's hello and the extension's pings reach the agent over the link.
  await agent.waitForMessage((m) => m.type === 'hello');
});
