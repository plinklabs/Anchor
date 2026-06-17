// Capture the teacher dashboard's key pages as website screenshots (#250).
//
// The dashboard can't be shot live — it needs MSAL sign-in, the backend, and a
// SignalR connection. So we build a demo web bundle from `lib/main_demo.dart`
// (auth bypassed, in-memory fakes seeded with deterministic demo data, a stub
// SignalR feed — no backend, no real auth, no secrets), serve it, and drive the
// real app route-by-route with Playwright, screenshotting each page.
//
// go_router uses the hash URL strategy on web, so every route is reachable as
// `#/...` against the served index — no server-side rewrites needed.
//
//   node screenshots/generate-screenshots.mjs            (rebuilds the bundle)
//   node screenshots/generate-screenshots.mjs --no-build (reuse build/web-demo)
//
// Output → ../website/assets/dashboard-*.png

import { chromium } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { createReadStream, mkdirSync, statSync } from 'node:fs';
import { createServer } from 'node:http';
import { dirname, join, normalize } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const dashboardDir = join(here, '..');
const bundleDir = join(dashboardDir, 'build', 'web-demo');
const outDir = join(dashboardDir, '..', 'website', 'assets');

// Fixed 16:10 desktop frame — a clean, repeatable canvas for the website.
const VIEWPORT = { width: 1440, height: 900 };

// The named PNG set. Each entry drives the real app to a route and shoots it.
// `settle` is extra quiet time after fonts load — the live view replays a fixed
// event feed and the home view animates its LIVE spark in, so they get a beat
// to land in frame. Output is otherwise deterministic (fixed demo data, fixed
// viewport, no real clock in the rendered content).
const SHOTS = [
  { name: 'dashboard-home', hash: '#/', settle: 600 },
  { name: 'dashboard-session', hash: `#/session/demo-session-3b`, settle: 900 },
  // Open the seeded bundle so the editor + its allowlist render, rather than
  // the empty "Select a bundle" pane — real navigation, the way a teacher lands.
  // Flutter web paints to a canvas (no DOM text to target), so click the
  // catalogue row by its stable on-canvas position in this fixed viewport.
  { name: 'dashboard-bundles', hash: '#/bundles', click: { x: 90, y: 262 }, settle: 700 },
  { name: 'dashboard-classes', hash: '#/classes', settle: 700 },
  { name: 'dashboard-history', hash: '#/history', settle: 600 },
  { name: 'dashboard-past-session', hash: `#/history/demo-past-session-3b`, settle: 700 },
];

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.svg': 'image/svg+xml',
  '.ico': 'image/x-icon',
  '.wasm': 'application/wasm',
  '.woff2': 'font/woff2',
  '.woff': 'font/woff',
  '.ttf': 'font/ttf',
  '.otf': 'font/otf',
  '.map': 'application/json',
};

function buildBundle() {
  console.log('building demo web bundle (lib/main_demo.dart)…');
  execFileSync(
    'flutter',
    ['build', 'web', '--target', 'lib/main_demo.dart', '--output', 'build/web-demo'],
    { cwd: dashboardDir, stdio: 'inherit', shell: process.platform === 'win32' },
  );
}

function startServer() {
  const server = createServer((req, res) => {
    let path = normalize(decodeURIComponent((req.url || '/').split('?')[0]))
      .replace(/^(\.\.[/\\])+/, '');
    if (path === '/' || path === '\\' || path === '') path = '/index.html';
    const file = join(bundleDir, path);
    if (!file.startsWith(bundleDir)) {
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

const shouldBuild = !process.argv.includes('--no-build');
if (shouldBuild) buildBundle();

mkdirSync(outDir, { recursive: true });

const { server, port } = await startServer();
const base = `http://127.0.0.1:${port}`;
const browser = await chromium.launch();

try {
  for (const shot of SHOTS) {
    const ctx = await browser.newContext({ viewport: VIEWPORT, deviceScaleFactor: 1 });
    const p = await ctx.newPage();
    // Flutter web boots the app at the hash route; loading the URL directly
    // lands the router on the right page on first paint.
    await p.goto(`${base}/${shot.hash}`, { waitUntil: 'load' });
    // Wait for the Flutter app to actually paint (the bootstrap removes nothing
    // visible, so key off a stable on-page string the shell always renders).
    await p.waitForFunction(
      () => document.body && document.body.innerText.length > 0,
      { timeout: 30_000 },
    ).catch(() => {});
    await p.evaluate(() => document.fonts.ready);
    await p.waitForTimeout(shot.settle);
    if (shot.click) {
      await p.mouse.click(shot.click.x, shot.click.y);
      await p.waitForTimeout(500); // let the editor pane paint in
    }
    const path = join(outDir, `${shot.name}.png`);
    await p.screenshot({
      path,
      clip: { x: 0, y: 0, width: VIEWPORT.width, height: VIEWPORT.height },
    });
    await ctx.close();
    console.log(`wrote ${shot.name}.png`);
  }
} finally {
  await browser.close();
  server.close();
}
