// Render the Edge Add-ons store listing images from the Anchor brand SVGs
// (design/anchor-mark.svg, design/anchor-lockup-light.svg) at the exact pixel
// sizes the dashboard requires. Uses the Playwright Chromium already installed
// for the extension e2e — it rasterises SVG + the Fraunces wordmark far better
// than ImageMagick's built-in SVG renderer.
//
//   node store-listing/generate-assets.mjs
//
// Output → store-listing/*.png

import { chromium } from '@playwright/test';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const outDir = dirname(fileURLToPath(import.meta.url));

const PAPER = '#FAF7F2';
const INK = '#1B1B23';
const INDIGO = '#34357A';
const MUTED = '#9A958B';

// The anchor mark (indigo on paper) — the open Plink ping ring as the shackle.
const MARK = `
<svg viewBox="0 0 56 56" xmlns="http://www.w3.org/2000/svg" role="img" aria-label="Anchor">
  <g fill="none" stroke="${INDIGO}" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
    <circle cx="28" cy="13" r="7"/>
    <circle cx="28" cy="13" r="1.6" fill="${INDIGO}" stroke="none"/>
    <line x1="17" y1="24" x2="39" y2="24"/>
    <line x1="28" y1="20" x2="28" y2="46"/>
    <path d="M12 38 Q28 54 44 38"/>
    <path d="M12 38 L9.5 33"/>
    <path d="M44 38 L46.5 33"/>
  </g>
</svg>`;

const fontsHead = `
  <link rel="preconnect" href="https://fonts.googleapis.com">
  <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
  <link href="https://fonts.googleapis.com/css2?family=Fraunces:opsz,wght@9..144,300..600&family=Space+Mono&display=swap" rel="stylesheet">`;

/** A full-bleed paper canvas with centered content. */
function page(w, h, inner, extraCss = '') {
  return `<!DOCTYPE html><html><head><meta charset="utf-8">${fontsHead}
  <style>
    *{margin:0;padding:0;box-sizing:border-box}
    html,body{width:${w}px;height:${h}px}
    body{background:${PAPER};display:flex;align-items:center;justify-content:center;
         font-family:'Fraunces',Georgia,serif;overflow:hidden}
    .wordmark{font-family:'Fraunces',Georgia,serif;color:${INK};font-weight:560;letter-spacing:-0.02em;line-height:1}
    .tag{font-family:'Space Mono',monospace;color:${MUTED};letter-spacing:0.04em}
    ${extraCss}
  </style></head><body>${inner}</body></html>`;
}

const TAGLINE = 'Classroom focus, made by teachers';

const assets = [
  {
    // Required. Just the mark on paper, generous padding, 1:1.
    name: 'logo-300.png',
    w: 300,
    h: 300,
    html: page(300, 300, `<div style="width:188px;height:188px">${MARK}</div>`),
  },
  {
    // Small promotional tile — lockup (mark + wordmark) over a tagline.
    name: 'promo-small-440x280.png',
    w: 440,
    h: 280,
    html: page(
      440,
      280,
      `<div style="display:flex;flex-direction:column;align-items:center;gap:18px">
         <div style="display:flex;align-items:center;gap:14px">
           <div style="width:58px;height:58px">${MARK}</div>
           <span class="wordmark" style="font-size:52px">anchor</span>
         </div>
         <span class="tag" style="font-size:13px">${TAGLINE}</span>
       </div>`,
    ),
  },
  {
    // Large promotional tile — same lockup, more air.
    name: 'promo-large-1400x560.png',
    w: 1400,
    h: 560,
    html: page(
      1400,
      560,
      `<div style="display:flex;flex-direction:column;align-items:center;gap:40px">
         <div style="display:flex;align-items:center;gap:34px">
           <div style="width:150px;height:150px">${MARK}</div>
           <span class="wordmark" style="font-size:140px">anchor</span>
         </div>
         <span class="tag" style="font-size:30px">${TAGLINE}</span>
       </div>`,
    ),
  },
];

const browser = await chromium.launch();
try {
  for (const a of assets) {
    const ctx = await browser.newContext({
      viewport: { width: a.w, height: a.h },
      deviceScaleFactor: 1,
    });
    const p = await ctx.newPage();
    await p.setContent(a.html, { waitUntil: 'networkidle' });
    await p.evaluate(() => document.fonts.ready);
    const file = join(outDir, a.name);
    await p.screenshot({ path: file, clip: { x: 0, y: 0, width: a.w, height: a.h } });
    await ctx.close();
    console.log(`wrote ${a.name} (${a.w}x${a.h})`);
  }
} finally {
  await browser.close();
}
