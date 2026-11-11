// #444 — the teacher's roster shows a student whose agent never joined.
//
// The agent and the extension each join a session when SessionStarted reaches
// them. In a classroom test two laptops had only the extension join: their
// students read as joined on the roster for the whole lesson, while nothing
// blocked apps on those laptops. The backend now waits for the agent's first
// heartbeat after the join, and past a grace period with none it shows the
// student as agent missing.
//
// This harness runs the real extension against the real backend with no agent,
// which is exactly that laptop. The backend it boots shortens the grace to 5 s
// (run-backend.ts); the 30 s default only makes this spec slower.

import { test, expect } from '../fixtures.ts';
import { expectRedirectedToBlockPage } from '../asserts.ts';
import { OFFLIST_HOST, STUDENT_DISPLAY_NAME } from '../config.ts';

test("a student whose extension joined without the agent shows as agent missing on the teacher's roster", async ({
  ext,
  backend,
  staticServer,
}) => {
  // Room for a backend reused from a local run, which has the 30 s default.
  test.setTimeout(90_000);
  await ext.configure();

  const page = await ext.context.newPage();
  await page.goto(staticServer.url(OFFLIST_HOST, '/before-session'));

  const classId = await backend.findClassId();
  const session = await backend.startSession(classId, []);

  // The extension joined and enforces the session.
  await expectRedirectedToBlockPage(page, ext, OFFLIST_HOST);

  const student = async () => {
    const rows = await backend.roster(session.id);
    const row = rows.find((r) => r.displayName === STUDENT_DISPLAY_NAME);
    if (!row) throw new Error(`${STUDENT_DISPLAY_NAME} is not on the roster: ${JSON.stringify(rows)}`);
    return row;
  };

  // No agent heartbeat arrives, so once the grace passes the backend's
  // heartbeat monitor says so, and the roster the dashboard loads shows it.
  await expect.poll(async () => (await student()).state, { timeout: 45_000 }).toBe('AgentMissing');
  // The extension keeps pinging all along: no silent-extension tamper flag.
  expect((await student()).tampered).toBe(false);
});
