// A loopback TCP relay between the extension and the e2e backend that a spec
// can cut and restore (#374): the student's network dropping out, or their
// laptop going to sleep, while the backend runs on. The agent's integration
// tests have the same thing (agent/tests/FocusAgent.IntegrationTests/
// NetworkRelay.cs, #354).
//
// Point the extension at `url` (configure({ backendUrl: relay.url })) and keep
// driving the backend directly with BackendClient: the teacher's side carries on
// while the extension can't hear it. The e2e backend itself is booted once per
// run by Playwright's webServer and can't be stopped from a spec, and a backend
// that is down couldn't start or end a session anyway.

import net from 'node:net';
import type { AddressInfo } from 'node:net';
import { BACKEND_PORT } from './config.ts';

export interface NetworkRelay {
  /** The backend URL to hand the extension. */
  readonly url: string;
  /** Resets every open connection and refuses new ones until restore(): the
   *  hub's WebSocket dies as it does on a lost network, and every attempt to
   *  come back fails at once. */
  cut(): void;
  /** Lets connections through again. */
  restore(): void;
  close(): Promise<void>;
}

export async function startNetworkRelay(backendPort: number = BACKEND_PORT): Promise<NetworkRelay> {
  let isCut = false;
  const links = new Set<[net.Socket, net.Socket]>();

  const server = net.createServer((extensionSide) => {
    if (isCut) {
      reset(extensionSide);
      return;
    }
    const backendSide = net.connect(backendPort, '127.0.0.1');
    const link: [net.Socket, net.Socket] = [extensionSide, backendSide];
    links.add(link);
    // A failure on either side ends the link the way a lost network would; a
    // graceful end is passed on by pipe().
    const fail = (): void => {
      links.delete(link);
      reset(extensionSide);
      reset(backendSide);
    };
    extensionSide.on('error', fail);
    backendSide.on('error', fail);
    extensionSide.on('close', () => links.delete(link));
    backendSide.on('close', () => links.delete(link));
    extensionSide.pipe(backendSide);
    backendSide.pipe(extensionSide);
  });

  await new Promise<void>((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  const port = (server.address() as AddressInfo).port;

  const cut = (): void => {
    isCut = true;
    for (const [extensionSide, backendSide] of links) {
      reset(extensionSide);
      reset(backendSide);
    }
    links.clear();
  };

  return {
    url: `http://127.0.0.1:${port}`,
    cut,
    restore() {
      isCut = false;
    },
    close() {
      cut();
      return new Promise<void>((resolve) => server.close(() => resolve()));
    },
  };
}

/** Close with a TCP reset rather than a graceful FIN, as a lost network would. */
function reset(socket: net.Socket): void {
  if (socket.destroyed) return;
  socket.on('error', () => {});
  socket.resetAndDestroy();
}
