// Capture the two student-facing surfaces — the block page and the toolbar
// status popup — as Edge Add-ons store screenshots (1280x800). Both are plain
// pages under dist/ that read their state from the URL (block page) or
// chrome.storage.session (popup), so we serve dist/ over http and inject a
// minimal `chrome` stub with a representative active session. No backend or real
// extension context needed — this renders the actual shipped HTML/CSS/fonts.
//
//   node store-listing/generate-screenshots.mjs   (run `npm run build` first)
//
// Output → store-listing/screenshot-*.png

import { chromium } from '@playwright/test';
import { createReadStream, statSync } from 'node:fs';
import { createServer } from 'node:http';
import { dirname, join, normalize } from 'node:path';
import { fileURLToPath } from 'node:url';

const outDir = dirname(fileURLToPath(import.meta.url));
const distDir = join(outDir, '..', 'dist');

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json',
  '.png': 'image/png',
  '.svg': 'image/svg+xml',
  '.woff2': 'font/woff2',
  '.woff': 'font/woff',
  '.map': 'application/json',
};

// A representative active session for a year-9 class — enough allowlist entries
// to show the list, with a join code, so the popup paints its full "active" face.
const DEMO_SESSION = {
  sessionId: 'demo-session',
  classId: 'class-3b',
  joinCode: 'PLINK-3B',
  startedAt: new Date().toISOString(),
  domains: [
    { matchType: 'Suffix', value: 'wikipedia.org' },
    { matchType: 'Suffix', value: 'smartschool.be' },
    { matchType: 'Suffix', value: 'geogebra.org' },
    { matchType: 'Suffix', value: 'classroom.google.com' },
  ],
};

// Injected before any page/iframe script runs. Covers what the block page
// (runtime) and the popup (storage.session) touch at load.
const stub = `
  globalThis.chrome = {
    runtime: {
      id: 'demo',
      onMessage: { addListener: () => {} },
      sendMessage: async () => ({ ok: true }),
      getURL: (p) => p,
    },
    storage: {
      session: { get: async () => ({ activeSession: ${JSON.stringify(DEMO_SESSION)} }) },
      local: { get: async () => ({}) },
    },
  };`;

function startServer() {
  const server = createServer((req, res) => {
    const path = normalize(decodeURIComponent((req.url || '/').split('?')[0])).replace(/^(\.\.[/\\])+/, '');
    const file = join(distDir, path);
    if (!file.startsWith(distDir)) {
      res.writeHead(403).end();
      return;
    }
    try {
      if (!statSync(file).isFile()) throw new Error('not a file');
    } catch {
      res.writeHead(404).end('not found');
      return;
    }
    const ext = file.slice(file.lastIndexOf('.'));
    res.writeHead(200, { 'content-type': MIME[ext] || 'application/octet-stream' });
    createReadStream(file).pipe(res);
  });
  return new Promise((resolve) => {
    server.listen(0, '127.0.0.1', () => resolve({ server, port: server.address().port }));
  });
}

const { server, port } = await startServer();
const base = `http://127.0.0.1:${port}`;
const browser = await chromium.launch();

try {
  // --- Block page -----------------------------------------------------------
  {
    const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 }, deviceScaleFactor: 1 });
    await ctx.addInitScript(stub);
    const p = await ctx.newPage();
    const blocked = encodeURIComponent('https://www.youtube.com/watch?v=dQw4w9WgXcQ');
    await p.goto(`${base}/block-page.html?blocked=${blocked}&session=demo-session`, { waitUntil: 'networkidle' });
    await p.evaluate(() => document.fonts.ready);
    await p.waitForTimeout(300); // let the ping animation settle into frame
    await p.screenshot({ path: join(outDir, 'screenshot-block-1280x800.png'), clip: { x: 0, y: 0, width: 1280, height: 800 } });
    await ctx.close();
    console.log('wrote screenshot-block-1280x800.png');
  }

  // --- Popup (framed on paper, as a floating card) --------------------------
  {
    const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 }, deviceScaleFactor: 1 });
    await ctx.addInitScript(stub);
    const p = await ctx.newPage();
    const frameHtml = `<!DOCTYPE html><html><head><meta charset="utf-8"><style>
        *{margin:0;box-sizing:border-box}
        html,body{width:1280px;height:800px}
        body{background:#FAF7F2;display:flex;align-items:center;justify-content:center}
        .card{box-shadow:0 28px 70px rgba(27,27,35,.28);border-radius:16px;overflow:hidden}
        iframe{width:320px;height:460px;border:0;display:block}
      </style></head><body>
        <div class="card"><iframe src="${base}/popup.html"></iframe></div>
      </body></html>`;
    await p.setContent(frameHtml, { waitUntil: 'networkidle' });
    const frame = p.frames().find((f) => f.url().includes('popup.html'));
    if (frame) {
      await frame.evaluate(() => document.fonts.ready);
      const h = await frame.evaluate(() => document.documentElement.scrollHeight || document.body.scrollHeight);
      await p.evaluate((height) => { document.querySelector('iframe').style.height = height + 'px'; }, h);
      await p.waitForTimeout(200);
    }
    await p.screenshot({ path: join(outDir, 'screenshot-popup-1280x800.png'), clip: { x: 0, y: 0, width: 1280, height: 800 } });
    await ctx.close();
    console.log('wrote screenshot-popup-1280x800.png');
  }
} finally {
  await browser.close();
  server.close();
}
