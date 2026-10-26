// #462 — the extension stops filtering for a session the student is out of.
//
// Leaving a session ("Leave session" in the agent), declining it, or quitting an
// older agent from the tray (AgentKilled) takes the student out of a session
// that runs on. Only the teacher's roster heard about it: the extension went on
// blocking off-list sites and pinging the session every 10 s, each ping turned
// away by the hub, until it reconnected or the session ended. Now the backend
// tells the student's user group (ParticipationEnded), and the extension drops
// the session as if it had ended: off-list sites load, tabs parked on the block
// page go back to their pages, and the pings stop. An extension that wasn't
// connected at that moment finds out when it is back, because the backend says
// the student is no longer in the session. Joining again by code brings the
// filtering back, through the SessionStarted it sends.
//
// The harness runs no agent: StudentAgent makes the agent's hub calls as the
// seeded student.

import { test as base, expect } from '../fixtures.ts';
import { expectRedirectedToBlockPage } from '../asserts.ts';
import { OFFLIST_HOST } from '../config.ts';
import { StudentAgent, type AgentExit } from '../student-agent.ts';
import type { BackendClient, StartedSession } from '../backend.ts';
import type { LoadedExtension } from '../extension.ts';
import type { StaticServer } from '../static-server.ts';
import type { Page } from '@playwright/test';

const test = base.extend<{ agent: StudentAgent }>({
  agent: async ({}, use) => {
    const agent = await StudentAgent.connect();
    await use(agent);
    await agent.stop();
  },
});

/** What the extension logs when the hub turns one of its pings away. */
const PING_REJECTED = 'ExtensionHeartbeat failed';
/** Long enough for the extension's 10 s heartbeat to tick at least once. */
const ONE_HEARTBEAT_MS = 12_000;

const EXITS: ReadonlyArray<{ how: AgentExit; doing: string }> = [
  { how: 'leave', doing: 'leaving the session' },
  { how: 'decline', doing: 'declining the session' },
  { how: 'quit', doing: 'quitting an older agent from the tray' },
];

test('leaving the session releases the blocked tab, lets off-list sites load and stops the pings, and joining again by code blocks them again', async ({
  ext,
  backend,
  staticServer,
  agent,
}) => {
  const { page, session } = await startSessionWithABlockedTab(ext, backend, staticServer);

  await agent.exit(session.id, 'leave');

  await expectReleased(page, ext);
  const offlist = await expectOfflistLoads(ext, staticServer);
  // Nothing pings the session any more, so the hub has nothing to turn away.
  const rejected = ext.countLogs(PING_REJECTED);
  await offlist.waitForTimeout(ONE_HEARTBEAT_MS);
  expect(ext.countLogs(PING_REJECTED)).toBe(rejected);

  // "Join with code" in the agent: the session's start reaches the extension
  // again, and its scan blocks both off-list tabs.
  await backend.joinByCode(session.joinCode);
  await expectRedirectedToBlockPage(offlist, ext, OFFLIST_HOST);
  await expectRedirectedToBlockPage(page, ext, OFFLIST_HOST);
  const { activeSession } = await ext.getSessionStorage<{ activeSession?: { sessionId: string } }>('activeSession');
  expect(activeSession?.sessionId).toBe(session.id);
});

for (const { how, doing } of EXITS.filter((e) => e.how !== 'leave')) {
  test(`${doing} releases the blocked tab and lets off-list sites load`, async ({
    ext,
    backend,
    staticServer,
    agent,
  }) => {
    const { page, session } = await startSessionWithABlockedTab(ext, backend, staticServer);

    await agent.exit(session.id, how);

    await expectReleased(page, ext);
    await expectOfflistLoads(ext, staticServer);
  });
}

for (const { how, doing } of EXITS) {
  test(`${doing} while the extension's service worker is stopped releases the blocked tab once it is back`, async ({
    ext,
    backend,
    staticServer,
    agent,
  }) => {
    const { page, session } = await startSessionWithABlockedTab(ext, backend, staticServer);

    // What the backend sends then finds no hub connection to reach.
    await ext.restartServiceWorker(() => agent.exit(session.id, how));

    await expectReleased(page, ext);
    await expectOfflistLoads(ext, staticServer);
  });
}

async function startSessionWithABlockedTab(
  ext: LoadedExtension,
  backend: BackendClient,
  staticServer: StaticServer,
): Promise<{ page: Page; session: StartedSession }> {
  await ext.configure();

  const page = await ext.context.newPage();
  await page.goto(staticServer.url(OFFLIST_HOST, '/before-session'));

  // A session that doesn't cover the off-list tab, so it gets blocked. The
  // extension joined the session before it blocked the tab.
  const classId = await backend.findClassId();
  const bundleId = await backend.findBundleId();
  const session = await backend.startSession(classId, [bundleId]);
  await expectRedirectedToBlockPage(page, ext, OFFLIST_HOST);
  return { page, session };
}

async function expectReleased(page: Page, ext: LoadedExtension): Promise<void> {
  // Poll on *leaving* the block page: its URL embeds the off-list host too.
  await expect.poll(() => page.url(), { timeout: 15_000 }).not.toContain('block-page.html');
  expect(page.url()).toContain(OFFLIST_HOST);
  expect(page.url()).toContain('/before-session');
  const { activeSession } = await ext.getSessionStorage<{ activeSession?: unknown }>('activeSession');
  expect(activeSession).toBeUndefined();
}

/** Opens an off-list site in a new tab, and checks it loads. */
async function expectOfflistLoads(ext: LoadedExtension, staticServer: StaticServer): Promise<Page> {
  const offlist = await ext.context.newPage();
  await offlist.goto(staticServer.url(OFFLIST_HOST, '/after-leaving'));
  await expect(offlist.locator('#ok')).toHaveText('anchor-e2e');
  expect(offlist.url()).not.toContain('block-page.html');
  return offlist;
}
