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

  // #318: the blocked URL must be legible, not white-on-white. With the ink
  // tokens resolved it renders as on-ink text (#FAF7F2) on the inset panel
  // (paper-3 #2B2B38) — a clear light-on-dark pair, not inherited-and-hoped-for.
  const blockedUrl = page.locator('[data-blocked-url]');
  await expect(blockedUrl).toHaveCSS('color', 'rgb(250, 247, 242)');
  await expect(blockedUrl).toHaveCSS('background-color', 'rgb(43, 43, 56)');

  // AE1 (#177): the calm focus-session mark is the signature concentric-ring
  // ping (not the old static dot). It renders, in the on-ink magenta, and is
  // actually animating — the real-browser proof the living mark is wired, not
  // a frozen ring. We check the core (a filled disc) for the on-ink magenta…
  const ping = page.locator('.pl-ping');
  await expect(ping).toHaveCount(1);
  await expect(page.locator('.pl-eyebrow__dot')).toHaveCount(0);
  await expect(ping.locator('.pl-ping__core')).toHaveCSS(
    'background-color',
    'rgb(236, 72, 153)',
  );
  // …and confirm a ring is mid-animation: sample its transform twice and
  // assert it moved (the pulse scales the ring over time).
  const ring = ping.locator('.pl-ping__ring').first();
  const t1 = await ring.evaluate((el) => getComputedStyle(el).transform);
  await page.waitForTimeout(250);
  const t2 = await ring.evaluate((el) => getComputedStyle(el).transform);
  expect(t1).not.toBe(t2);
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
