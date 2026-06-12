// A tiny loopback HTTP server the specs navigate to instead of the public
// internet. It answers any path/host with a trivial 200 page, so the same
// socket can stand in for the on-list, bundle-only, and off-list hosts — Edge
// is told to resolve all of them to 127.0.0.1 (see config.MAPPED_HOSTS).
//
// Why a real server at all: the *blocked* assertions don't need a reachable
// page (the redirect fires in webNavigation.onBeforeNavigate, before the
// request), but the *not-blocked* assertions do — an on-list tab has to
// actually land somewhere we can read back, with no flaky DNS in the loop.

import http from 'node:http';
import type { AddressInfo } from 'node:net';

export interface StaticServer {
  readonly port: number;
  /** Build a URL on this server for the given host (host is preserved in the
   *  URL so Edge's resolver remaps it; the server itself ignores it). */
  url(host: string, pathname?: string): string;
  close(): Promise<void>;
}

const PAGE = (host: string, url: string) =>
  `<!doctype html><html lang="en"><head><meta charset="utf-8">` +
  `<title>anchor-e2e</title></head><body>` +
  `<h1 id="ok">anchor-e2e</h1>` +
  `<p id="where">${host}${url}</p></body></html>`;

export async function startStaticServer(): Promise<StaticServer> {
  const server = http.createServer((req, res) => {
    res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
    res.end(PAGE(req.headers.host ?? '', req.url ?? '/'));
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
