// A tiny loopback HTTP server the specs navigate to instead of the public
// internet. It answers any path/host with a trivial 200 page (bar the web-font
// page and its subresources, below), so the same
// socket can stand in for the on-list, bundle-only, and off-list hosts — Edge
// is told to resolve all of them to 127.0.0.1 (see config.MAPPED_HOSTS).
//
// Why a real server at all: the *blocked* assertions don't need a reachable
// page (the redirect fires in webNavigation.onBeforeNavigate, before the
// request), but the *not-blocked* assertions do — an on-list tab has to
// actually land somewhere we can read back, with no flaky DNS in the loop.

import fs from 'node:fs';
import http from 'node:http';
import type { AddressInfo } from 'node:net';
import path from 'node:path';
import type { BrowserContext } from '@playwright/test';
import { EXTENSION_ROOT, WEB_FONT_CSS_HOST, WEB_FONT_FILE_HOST } from './config.ts';

export interface StaticServer {
  readonly port: number;
  /** Build a URL on this server for the given host (host is preserved in the
   *  URL so Edge's resolver remaps it; the server itself ignores it). */
  url(host: string, pathname?: string): string;
  close(): Promise<void>;
}

const PAGE = (host: string, url: string, head = '') =>
  `<!doctype html><html lang="en"><head><meta charset="utf-8">` +
  `<title>anchor-e2e</title>${head}</head><body>` +
  `<h1 id="ok">anchor-e2e</h1>` +
  `<p id="where">${host}${url}</p></body></html>`;

// A page that uses a web font the way a Google Fonts embed does (#516): its
// stylesheet comes from WEB_FONT_CSS_HOST and points at a font file on
// WEB_FONT_FILE_HOST. Both are subresources of the page, so the session's
// allowlist needn't list either host. Paths as Google serves them.
export const WEB_FONT_PAGE_PATH = '/web-font';
/** The family the web-font page's stylesheet declares. */
export const WEB_FONT_FAMILY = 'Anchor E2E Web Font';
const WEB_FONT_CSS_PATH = '/css2';
const WEB_FONT_FILE_PATH = '/s/anchore2e/v1/font.woff2';
// Any real woff2 does; this one ships with the extension.
const WEB_FONT_FILE = path.join(
  EXTENSION_ROOT,
  'src/vendor/plink-design-system/assets/fonts/hanken-grotesk/HankenGrotesk-Variable.woff2',
);

/**
 * Answer https:// navigations to `hosts` from inside the browser, with the page
 * the static server serves (#531). For hosts this server can't stand in for:
 * every host under .microsoft (m365.cloud.microsoft ...) is on Edge's HSTS
 * preload list, so Edge turns an http:// URL there into https://, which this
 * server doesn't speak. Playwright answers the request before it reaches the
 * network; the extension sees the navigation as it would any other. Playwright
 * answers only a navigation's first request, never one a redirect leads to, so
 * navigate to these hosts directly.
 */
export async function serveOverHttps(context: BrowserContext, hosts: readonly string[]): Promise<void> {
  await context.route(
    (url) => url.protocol === 'https:' && hosts.includes(url.hostname),
    (route) => {
      const url = new URL(route.request().url());
      return route.fulfill({
        status: 200,
        contentType: 'text/html; charset=utf-8',
        body: PAGE(url.host, url.pathname + url.search),
      });
    },
  );
}

/**
 * Answer https:// navigations to each host in `redirects` from inside the
 * browser with a redirect to the URL it maps that host to (#532), the way
 * Microsoft answers a typed outlook.com. The redirect's target is a request
 * Playwright doesn't answer (see serveOverHttps), so point it at a mapped host
 * on the static server.
 */
export async function redirectOverHttps(
  context: BrowserContext,
  redirects: Readonly<Record<string, string>>,
): Promise<void> {
  await context.route(
    (url) => url.protocol === 'https:' && Object.hasOwn(redirects, url.hostname),
    (route) => {
      const url = new URL(route.request().url());
      return route.fulfill({ status: 301, headers: { location: redirects[url.hostname] } });
    },
  );
}

export async function startStaticServer(): Promise<StaticServer> {
  const origin = (host: string) => `http://${host}:${(server.address() as AddressInfo).port}`;

  const server = http.createServer((req, res) => {
    const pathname = new URL(req.url ?? '/', 'http://e2e').pathname;
    if (pathname === WEB_FONT_CSS_PATH) {
      res.writeHead(200, { 'content-type': 'text/css; charset=utf-8' });
      res.end(
        `@font-face { font-family: '${WEB_FONT_FAMILY}'; ` +
          `src: url(${origin(WEB_FONT_FILE_HOST)}${WEB_FONT_FILE_PATH}) format('woff2'); } ` +
          `#ok { font-family: '${WEB_FONT_FAMILY}', monospace; }`,
      );
      return;
    }
    if (pathname === WEB_FONT_FILE_PATH) {
      // A font is fetched in CORS mode, so a cross-origin one needs this header,
      // as fonts.gstatic.com sends it.
      res.writeHead(200, { 'content-type': 'font/woff2', 'access-control-allow-origin': '*' });
      res.end(fs.readFileSync(WEB_FONT_FILE));
      return;
    }
    const head =
      pathname === WEB_FONT_PAGE_PATH
        ? `<link rel="stylesheet" href="${origin(WEB_FONT_CSS_HOST)}${WEB_FONT_CSS_PATH}?family=AnchorE2E">`
        : '';
    res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
    res.end(PAGE(req.headers.host ?? '', req.url ?? '/', head));
  });

  await new Promise<void>((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });

  const port = (server.address() as AddressInfo).port;

  return {
    port,
    url(host: string, pathname = '/') {
      const suffix = pathname.startsWith('/') ? pathname : `/${pathname}`;
      return `http://${host}:${port}${suffix}`;
    },
    close() {
      return new Promise<void>((resolve, reject) =>
        server.close((err) => (err ? reject(err) : resolve())),
      );
    },
  };
}
