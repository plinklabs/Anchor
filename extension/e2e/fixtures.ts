// Playwright fixtures composing the harness pieces. Each test gets a fresh
// extension (its own Edge profile + service worker, so no cached session
// leaks between specs) and a backend client that ends the sessions the test
// started; the static server is cheap and shared per worker.

import { test as base } from '@playwright/test';
import { BackendClient } from './backend.ts';
import { loadExtension, type LoadedExtension } from './extension.ts';
import { startStaticServer, type StaticServer } from './static-server.ts';

interface TestFixtures {
  backend: BackendClient;
  ext: LoadedExtension;
}

interface WorkerFixtures {
  staticServer: StaticServer;
}

export const test = base.extend<TestFixtures, WorkerFixtures>({
  // One static server per worker — it's stateless, so sharing is safe.
  staticServer: [
    async ({}, use) => {
      const server = await startStaticServer();
      await use(server);
      await server.close();
    },
    { scope: 'worker' },
  ],

  backend: async ({}, use) => {
    const backend = new BackendClient();
    await use(backend);
    // A session left running would reach the next spec's fresh extension
    // through its catch-up on connect (#356).
    await backend.endStartedSessions();
  },

  ext: async ({}, use) => {
    const extension = await loadExtension();
    await use(extension);
    await extension.close();
  },
});

export { expect } from '@playwright/test';
