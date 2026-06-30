// i18n (#322) — the real-browser proof the unit/structure tests can't give:
// launch Edge with a Dutch UI language (`--lang=nl`) and confirm chrome.i18n
// actually selects the `_locales/nl` catalogue, so the block page and popup
// render Dutch — both the static HTML copy (localizeDocument) and the dynamic
// status strings routed through t(). The English path is covered end-to-end by
// the theme specs (they assert the English idle headline), which run in the
// host-default locale.
//
// Neither surface needs a session for this check: the block page renders its
// static copy from the URL params alone, and the popup shows its idle face when
// no session is cached — so this spec drives no backend, just the loaded pages.

import { test, expect } from '@playwright/test';
import { loadExtension, type LoadedExtension } from '../extension.ts';

let ext: LoadedExtension;

test.beforeAll(async () => {
  ext = await loadExtension({ locale: 'nl' });
});

test.afterAll(async () => {
  await ext?.close();
});

test('the block page renders Dutch under a Dutch browser', async () => {
  const page = await ext.context.newPage();
  // Open the block page directly with no `session` param: that exercises both
  // the static localized copy and the dynamic "no session" status (t()).
  await page.goto(`${ext.blockPagePrefix}?blocked=https://example.com/maths`);

  await expect(page.locator('h1')).toHaveText('Even gefocust blijven');
  await expect(page.locator('[data-action="request"]')).toHaveText('Toegang vragen');
  await expect(page.locator('[data-action="back"]')).toHaveText('Terug');
  await expect(page.locator('.footnote')).toHaveText('Anchor focusbewaking');
  // The dynamic status string (set from block-page.ts via t()) is Dutch too.
  await expect(page.locator('[data-status]')).toHaveText('Geen actieve sessie — herlaad de pagina.');
  // <html lang> is stamped to the resolved UI language for assistive tech.
  await expect(page.locator('html')).toHaveAttribute('lang', /^nl/);

  await page.close();
});

test('the popup idle face renders Dutch under a Dutch browser', async () => {
  const page = await ext.context.newPage();
  await page.goto(`chrome-extension://${ext.extensionId}/popup.html`);
  await page.waitForFunction(() => document.querySelector('[data-popup-root]') !== null);

  // No session cached → idle face, in Dutch.
  await expect(page.locator('[data-when="idle"] h1')).toHaveText('Geen actieve sessie');
  // The idle eyebrow label is set dynamically from popup.ts via t().
  await expect(page.locator('[data-eyebrow-label]')).toHaveText('Inactief');

  await page.close();
});
