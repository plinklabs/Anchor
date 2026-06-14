// AF3 (#164) — the block page consumes the design-system vanilla binding
// (vendored dist/plink.css + bundled fonts) and renders on the DS ink
// treatment, rather than the old hand-rolled inline styles.
//
// This is the real-browser check the unit/structure tests can't give: it loads
// the *actual* block page in Chrome with the extension's packaged CSS and fonts
// and asserts the binding is in force — the ink palette is applied, the bundled
// Space Mono webfont actually loaded (not a silent system fallback), the spark
// stays magenta, and the one per-product accent is Anchor's indigo. It fails if
// plink.css isn't wired, the font assets don't resolve, or the accent regresses.

import { test, expect } from '../fixtures.ts';
import { expectRedirectedToBlockPage } from '../asserts.ts';
import { OFFLIST_HOST } from '../config.ts';

test.beforeEach(async ({ ext, backend }) => {
  await ext.configure();
  const classId = await backend.findClassId();
  const bundleId = await backend.findBundleId();
  await backend.startSession(classId, [bundleId]);
  await ext.waitForLog('active session cached');
});

test('the block page renders on the DS ink theme with the bundled fonts', async ({
  ext,
  staticServer,
}) => {
  const offlistUrl = staticServer.url(OFFLIST_HOST, '/themed');
  const page = await ext.context.newPage();
  await page.goto(offlistUrl).catch(() => {});

  await expectRedirectedToBlockPage(page, ext, OFFLIST_HOST);

  // `.plink-ink` puts the surface on the full-bleed ink panel (#1B1B23).
  await expect(page.locator('body')).toHaveCSS('background-color', 'rgb(27, 27, 35)');

  // The bundled Space Mono webfont actually loaded — proves plink.css and its
  // `../assets/fonts/…` resolved, not a silent system-mono fallback. Check the
  // regular (400) face: that's the weight the eyebrow/footnote/url render in, so
  // it's the one the page actually requests (the 700 face may never load).
  await page.evaluate(() => document.fonts.ready);
  const monoLoaded = await page.evaluate(() =>
    document.fonts.check('16px "Space Mono"'),
  );
  expect(monoLoaded).toBe(true);

  // The spark stays magenta on ink (the on-ink brighter magenta #EC4899)…
  await expect(page.locator('button[data-action="back"]')).toHaveCSS(
    'background-color',
    'rgb(236, 72, 153)',
  );

  // …while the one identity element carries Anchor's reserved indigo accent
  // (#7E80D2 on ink — ANCHOR_BRAND.md §2), never magenta.
  await expect(page.locator('.pl-identity-rule')).toHaveCSS(
    'background-color',
    'rgb(126, 128, 210)',
  );
});

// AF4 (#165): the block page is student-facing, so its ink treatment is FIXED —
// it must not follow the OS light/dark setting (ANCHOR_BRAND.md §6). This is the
// real-browser proof of the rule: emulate a student on a light-themed machine
// and assert the page is still ink, and that `color-scheme: dark` pins the UA so
// native controls/scrollbars stay dark too. Fails if a `prefers-color-scheme`
// swap is ever reintroduced.
test('the block page stays ink even when the OS prefers a light theme', async ({
  ext,
  staticServer,
}) => {
  const offlistUrl = staticServer.url(OFFLIST_HOST, '/themed-light-os');
  const page = await ext.context.newPage();
  // Emulate a light OS theme before navigating — a system-following page would
  // flip to paper here.
  await page.emulateMedia({ colorScheme: 'light' });
  await page.goto(offlistUrl).catch(() => {});

  await expectRedirectedToBlockPage(page, ext, OFFLIST_HOST);

  // Still the full-bleed ink panel (#1B1B23), not paper.
  await expect(page.locator('body')).toHaveCSS('background-color', 'rgb(27, 27, 35)');
  // The UA colour-scheme is pinned dark, so native controls/scrollbars match.
  await expect(page.locator('body')).toHaveCSS('color-scheme', 'dark');
});
