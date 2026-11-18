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
