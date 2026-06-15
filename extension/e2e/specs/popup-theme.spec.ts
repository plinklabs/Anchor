// AE2 (#178) — the toolbar-action status popup is a student-facing surface, so
// it renders on the DS ink treatment (ANCHOR_BRAND.md §3), like the block page.
//
// This is the real-browser proof the unit/structure tests can't give: it loads
// the *actual* popup page in the browser with the extension's packaged CSS and
// fonts, against a *real* active session cached by the background worker, and
// asserts the binding is in force — the ink palette is applied, the spark stays
// magenta, the one per-product accent is Anchor's indigo, the living focus ping
// is animating, and the cached session's allowed sites are actually listed.

import { test, expect } from '../fixtures.ts';
import { BUNDLE_ONLY_HOST } from '../config.ts';

// The popup is just an extension page — open it by URL like any other.
function popupUrl(extensionId: string): string {
  return `chrome-extension://${extensionId}/popup.html`;
}

test('the popup renders the active session on the DS ink theme', async ({ ext, backend }) => {
  await ext.configure();
  const classId = await backend.findClassId();
  const bundleId = await backend.findBundleId();
  await backend.startSession(classId, [bundleId]);
  await ext.waitForLog('active session cached');

  const page = await ext.context.newPage();
  await page.goto(popupUrl(ext.extensionId));
  await page.waitForFunction(() =>
    document.querySelector('[data-popup-root]')?.classList.contains('is-active'),
  );

  // `.plink-ink` puts the surface on the full-bleed ink panel (#1B1B23).
  await expect(page.locator('body')).toHaveCSS('background-color', 'rgb(27, 27, 35)');
  // The UA colour-scheme is pinned dark so native scrollbars/controls match.
  await expect(page.locator('body')).toHaveCSS('color-scheme', 'dark');

  // The one identity element carries Anchor's reserved indigo accent (#7E80D2
  // on ink — ANCHOR_BRAND.md §2), never magenta.
  await expect(page.locator('.pl-identity-rule')).toHaveCSS(
    'background-color',
    'rgb(126, 128, 210)',
  );

  // The living focus-session mark: the on-ink magenta ping, actually animating.
  await page.evaluate(() => document.fonts.ready);
  const ping = page.locator('.pl-ping');
  await expect(ping).toHaveCount(1);
  await expect(ping.locator('.pl-ping__core')).toHaveCSS(
    'background-color',
    'rgb(236, 72, 153)',
  );
  const ring = ping.locator('.pl-ping__ring').first();
  const t1 = await ring.evaluate((el) => getComputedStyle(el).transform);
  await page.waitForTimeout(250);
  const t2 = await ring.evaluate((el) => getComputedStyle(el).transform);
  expect(t1).not.toBe(t2);

  // The cached session's allowed sites are actually listed — the bundle that
  // backs the seeded session includes BUNDLE_ONLY_HOST's suffix, so it shows up.
  const allowlist = page.locator('[data-allowlist]');
  await expect(allowlist).toBeVisible();
  await expect(allowlist.locator('li')).not.toHaveCount(0);
});

test('the popup shows the idle face when no session is active', async ({ ext }) => {
  // No session started — the worker comes up unconfigured, so storage.session
  // holds no active session. The popup must read that and show the idle copy.
  await ext.configure();

  const page = await ext.context.newPage();
  await page.goto(popupUrl(ext.extensionId));
  await page.waitForFunction(() =>
    document.querySelector('[data-popup-root]') !== null,
  );

  // Idle: the root never gains .is-active, so the active block is hidden and
  // the idle headline shows.
  await expect(page.locator('[data-popup-root]')).not.toHaveClass(/\bis-active\b/);
  await expect(page.locator('[data-when="idle"] h1')).toBeVisible();
  await expect(page.locator('[data-when="idle"] h1')).toHaveText('No active session');
  // Still ink, even idle.
  await expect(page.locator('body')).toHaveCSS('background-color', 'rgb(27, 27, 35)');

  // The ping is frozen (static), not pulsing, so the idle popup doesn't imply
  // an active session.
  await expect(page.locator('.pl-ping')).toHaveClass(/\bpl-ping--static\b/);
});
