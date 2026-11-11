// #121 — restore redirected tabs to their original URL when the session ends.
//
// When a session starts, an already-open off-list tab is redirected to the
// block page (#91). Once the session ends those tabs would otherwise sit on the
// block page until the student clicks "Go back". handleSessionEnded →
// restoreRedirectedTabs reads the original URL back off the block-page URL and
// navigates the tab home. Remove that call and this test times out waiting for
// the tab to leave block-page.html.
//
// #462, #489 — the end comes from the on-box agent, whether the teacher ended
// the session or the student left it, declined it or quit: either way the agent
// tells the extension the session is over for this student, and off-list sites
// load again.

import { test, expect } from '../fixtures.ts';
import { expectRedirectedToBlockPage } from '../asserts.ts';
import { OFFLIST_HOST, WITH_MS365 } from '../config.ts';

test('a tab redirected to the block page returns to its original url when the session ends', async ({
  ext,
  agent,
  staticServer,
}) => {
  const offlistUrl = staticServer.url(OFFLIST_HOST, '/before-session');
  const page = await ext.context.newPage();
  await page.goto(offlistUrl);

  // A session that doesn't cover the off-list tab → it gets redirected.
  agent.startSession(WITH_MS365);
  await expectRedirectedToBlockPage(page, ext, OFFLIST_HOST);

  // The session ends — the tab should navigate back to where it was, on its own.
  agent.endSession();

  // Poll on *leaving* the block page: the block-page URL itself embeds the
  // off-list host in its `blocked` param, so asserting OFFLIST_HOST is present
  // would pass before the restore even happens.
  await expect.poll(() => page.url(), { timeout: 15_000 }).not.toContain('block-page.html');
  expect(page.url()).toContain(OFFLIST_HOST);
  expect(page.url()).toContain('/before-session');

  // And it is really over: off-list sites load again.
  const later = await ext.context.newPage();
  await later.goto(staticServer.url(OFFLIST_HOST, '/after-session'));
  await expect(later.locator('#ok')).toHaveText('anchor-e2e');
  expect(later.url()).not.toContain('block-page.html');
});
