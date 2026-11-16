// Playwright fixtures composing the harness pieces. Each test gets a fresh
// stand-in for the on-box agent (its own pipe) and a fresh extension connected
// to it through the real native host (its own Edge profile + service worker, so
// no cached session leaks between specs); the static server is cheap and shared
// per worker.

import { test as base } from '@playwright/test';
import { loadExtension, type LoadedExtension } from './extension.ts';
import { FakeAgent } from './fake-agent.ts';
import { startStaticServer, type StaticServer } from './static-server.ts';
import { witnessHostSupported } from './witness-host.ts';

interface TestFixtures {
  agent: FakeAgent;
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

  agent: async ({}, use, testInfo) => {
    // The extension learns its session only from the agent, over native
    // messaging (#489), which needs Windows (HKCU host registration) — what the
    // Extension E2E CI job runs on.
    testInfo.skip(!witnessHostSupported(), 'the native witness host is Windows-only');
    const agent = await FakeAgent.start();
    await use(agent);
    await agent.close();
  },

  // Connected to the agent before the spec starts: the extension has been
  // handed the agent's state (no session, unless the spec set one up first).
  ext: async ({ agent }, use) => {
    const extension = await loadExtension({ agent });
    await agent.waitForConnection();
    await extension.waitForLog('witness port connected to agent host', 20_000);
    await use(extension);
    await extension.close();
  },
});

export { expect } from '@playwright/test';
