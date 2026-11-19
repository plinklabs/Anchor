// #524 — the extension checks its own settings and tells the agent.
//
// A student can change Anchor's settings on its page in Edge before a session
// starts. Nothing fires then, and the extension stays enabled with its link to
// the agent up, so the agent counted it as healthy. Now the extension reads its
// settings on every session the agent hands down and whenever they change, and
// sends them to the agent (`self_check`), which pauses Edge while Anchor may not
// work on all sites; the button on that pause has the extension open its own
// settings page (`open_extension_page`).
//
// The settings are changed the way the student changes them: with the controls
// on the extension's own page in Edge (edge://extensions/?id=…). What the agent
// does with the result is the agent e2e's part
// (agent/tests/FocusAgent.IntegrationTests/ExtensionSiteAccessTests.cs).

import { test, expect } from '../fixtures.ts';
import { BASELINE, BROWSER_CHANNEL } from '../config.ts';
import { allowInPrivate, openExtensionSettings, setSiteAccess } from '../extension-settings.ts';
import type { FakeAgent, ReceivedMessage } from '../fake-agent.ts';

// The controls are Edge's own extension page; Chrome's differ.
test.skip(!BROWSER_CHANNEL.startsWith('msedge'), "drives Edge's own extension settings page");

test('the extension tells the agent what its settings allow, when a session starts and whenever they change', async ({
  ext,
  agent,
}) => {
  // 1. A session starts on an untouched install: Anchor works on all sites, and
  //    isn't allowed in InPrivate, which is Edge's default for every extension.
  const sessionId = agent.startSession(BASELINE);
  expect(await nextSelfCheck(agent, 0)).toMatchObject({ sessionId, allSites: true, inPrivate: false });

  // 2. The student turns "Site access" down to "On click": the agent hears at
  //    once, with no restart of the extension or the session.
  const settings = await openExtensionSettings(ext);
  let from = agent.received.length;
  await setSiteAccess(settings, 'ON_CLICK');
  expect(await nextSelfCheck(agent, from)).toMatchObject({ sessionId, allSites: false });

  // 3. The student presses the button on the agent's pause: the extension opens
  //    its own page for them, which the agent can't do from outside Edge.
  agent.openExtensionPage();
  await expect
    .poll(() => ext.context.pages().filter((p) => p.url().includes(`extensions/?id=${ext.extensionId}`)).length)
    .toBe(2);

  // 4. Back to "On all sites": the check passes again, which lifts the pause.
  from = agent.received.length;
  await setSiteAccess(settings, 'ON_ALL_SITES');
  expect(await nextSelfCheck(agent, from)).toMatchObject({ sessionId, allSites: true });

  // 5. "Allow in InPrivate" restarts the extension; the session the agent hands
  //    the new connection brings the check that says so.
  from = agent.received.length;
  const connections = agent.connections;
  await allowInPrivate(settings);
  await agent.waitForConnection(connections + 1);
  expect(await nextSelfCheck(agent, from, 20_000)).toMatchObject({ sessionId, allSites: true, inPrivate: true });
});

test('outside a session the extension checks nothing, and opens no page', async ({ ext, agent }) => {
  const settings = await openExtensionSettings(ext);
  await setSiteAccess(settings, 'ON_CLICK');

  // The agent never asks outside a session; one that asks about a session the
  // extension isn't in (it ended meanwhile) opens nothing either.
  agent.send({ type: 'open_extension_page', sessionId: 'a-session-that-ended' });
  await ext.waitForLog('not opening the extension page for a session other than the active one');

  expect(agent.received.filter((m) => m.type === 'self_check')).toEqual([]);
  expect(ext.context.pages().filter((p) => p.url().includes('extensions/?id=')).length).toBe(1);
});

async function nextSelfCheck(agent: FakeAgent, from: number, timeout = 15_000): Promise<ReceivedMessage> {
  return agent.waitForMessage((m) => m.type === 'self_check', timeout, from);
}
