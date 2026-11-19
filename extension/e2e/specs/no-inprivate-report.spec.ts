// #539 — the extension sends no report of an InPrivate window of its own.
//
// It used to report every InPrivate window it saw open during a session
// (`tamper`, kind `inprivate_opened`), the moment it opened. It only saw them
// once allowed in InPrivate, so the agent's own InPrivate witness reports them
// (#148), and since #535 only one the student leaves open: the agent pauses
// such a window at once and drops the extension's report, which would tell the
// teacher of a window closed in time too. The extension stopped sending it.
//
// The window is opened where the extension sees it: with "Allow in InPrivate"
// on, the only setting it ever reported one in. That it filters the window's
// off-list page proves it saw it. What the agent does with an InPrivate window
// is the agent e2e's part
// (agent/tests/FocusAgent.IntegrationTests/InPrivateWitnessTests.cs,
// SchoolProfileTests.cs).

import type { BrowserContext, Worker } from '@playwright/test';
import { test, expect } from '../fixtures.ts';
import { BASELINE, BROWSER_CHANNEL, OFFLIST_HOST } from '../config.ts';
import { allowInPrivate, openExtensionSettings } from '../extension-settings.ts';

// "Allow in InPrivate" is switched on with Edge's own extension page.
test.skip(!BROWSER_CHANNEL.startsWith('msedge'), "drives Edge's own extension settings page");

test('an InPrivate window opened during a session is filtered, and the extension sends no report of it', async ({
  ext,
  agent,
  staticServer,
}) => {
  // 1. The extension is allowed in InPrivate. That restarts it; it reconnects.
  const settings = await openExtensionSettings(ext);
  const connections = agent.connections;
  await allowInPrivate(settings);
  await agent.waitForConnection(connections + 1);

  // 2. A session starts.
  const sessionId = agent.startSession(BASELINE);
  await ext.waitForLog('active session cached');

  // 3. The student opens an InPrivate window on an off-list page.
  const from = agent.received.length;
  const window = await openInPrivateWindow(ext.context, staticServer.url(OFFLIST_HOST, '/inprivate'));
  expect(window.incognito).toBe(true);

  // 4. The extension sees the window: it filters its page, and reports the block.
  const blocked = await agent.waitForMessage((m) => m.type === 'blocked_url', 15_000, from);
  expect(blocked).toMatchObject({ sessionId, host: OFFLIST_HOST });

  // 5. It sent no report of the window itself. The old report went out the
  //    moment the window opened (windows.onCreated), ahead of the block's.
  expect(agent.received.filter((m) => m.type === 'tamper')).toEqual([]);
});

/**
 * Open an InPrivate window on `url` from the extension's service worker, the
 * way Edge opens one for the student (Playwright opens no InPrivate window of
 * its own). The worker has just restarted, so the handle is taken fresh, and
 * taken again if it goes away under the call.
 */
async function openInPrivateWindow(
  context: BrowserContext,
  url: string,
): Promise<{ incognito: boolean; id?: number }> {
  let lastError: unknown;
  for (let attempt = 0; attempt < 3; attempt++) {
    const worker: Worker = context.serviceWorkers().at(-1) ?? (await context.waitForEvent('serviceworker'));
    try {
      return await worker.evaluate(async (u) => {
        const created = await chrome.windows.create({ incognito: true, url: u });
        return { incognito: created?.incognito === true, id: created?.id };
      }, url);
    } catch (err) {
      if (err instanceof Error && /Service worker restarted|Target .*closed/.test(err.message)) {
        lastError = err;
        continue;
      }
      throw err;
    }
  }
  throw lastError;
}
