// #572 — the agent link comes back on its own while the student isn't browsing.
//
// When the witness host can't be started (the agent being installed again, for
// one), the extension retries with a setTimeout backoff. Edge stops an idle MV3
// worker after about 30 s and the pending timer goes with it, so before #572 the
// link stayed down until something woke the worker: a navigation, a tab update,
// the browser starting. A repeating alarm now runs while the link is down, and an
// alarm wakes a stopped worker.
//
// Driven in real Edge: the extension starts with no host to start, its worker is
// stopped, the host comes back, and nothing navigates. The link must come back
// to the agent by itself, through a worker generation only the alarm started.
//
// #561: once back, that worker tells the agent why the link was down (Edge's
// reason from chrome.runtime.lastError, kept across the worker stop), and the
// host Edge started has logged that it reached the agent.

import { test, expect } from '@playwright/test';
import { EXTENSION_ID, WORKER_START_LOG, loadExtension } from '../extension.ts';
import { FakeAgent } from '../fake-agent.ts';
import { readWitnessHostLog, registerWitnessHost, witnessHostSupported } from '../witness-host.ts';

/** Either line the extension logs when the host can't be started (#243). */
const DEAD_LINK_LINES = ['witness port dropped before any host message', 'connectNative failed'];

test.skip(!witnessHostSupported(), 'the native witness host is Windows-only');

test('the link to the agent comes back with the worker stopped and no browsing (#572)', async () => {
  // The alarm fires every 30 s; allow for the one already running and a slow start.
  test.setTimeout(150_000);

  const agent = await FakeAgent.start();
  // Set before Edge starts: the host Edge launches later inherits Edge's env.
  const previousPipe = process.env.ANCHOR_WITNESS_PIPE;
  process.env.ANCHOR_WITNESS_PIPE = agent.pipeName;
  // No agent passed: the harness points the host registration at a manifest
  // that isn't there, so the host can't be started.
  const ext = await loadExtension();
  try {
    await expect
      .poll(() => DEAD_LINK_LINES.some((line) => ext.countLogs(line) > 0), { timeout: 20_000 })
      .toBe(true);

    await ext.stopServiceWorker();
    const startsWhileStopped = ext.countLogs(WORKER_START_LOG);

    // The host can be started again — and nothing the student does wakes the
    // worker to find out.
    const host = registerWitnessHost(EXTENSION_ID);
    try {
      await agent.waitForConnection(1, 90_000);
      await ext.waitForLog('witness port connected to agent host', 15_000);
      await ext.waitForLog('agent message: no_session', 15_000);
      // It was a new worker generation that came back: the alarm woke it.
      expect(ext.countLogs(WORKER_START_LOG)).toBeGreaterThan(startsWhileStopped);
      // No page was ever opened to wake it.
      expect(ext.context.pages().filter((p) => p.url() !== 'about:blank')).toEqual([]);

      // #561: the new worker tells the agent why the link was down: Edge's
      // reason, read off chrome.runtime.lastError by the worker that saw it,
      // and kept for this one across the stop.
      const linkError = await agent.waitForMessage((m) => m.type === 'link_error', 15_000);
      expect(linkError.error).toMatch(/native messaging host/i);
      expect(linkError.failedAttempts).toBeGreaterThanOrEqual(1);
      expect(Date.parse(String(linkError.since))).toBeLessThan(Date.now());

      // #561: and the host Edge started logged who started it and that it
      // reached the agent, in the folder the agent's log upload reads.
      const hostLog = readWitnessHostLog();
      expect(hostLog).toContain(`started by chrome-extension://${EXTENSION_ID}/; agent pipe ${agent.pipeName}.`);
      expect(hostLog).toContain(`Connected to the agent's pipe ${agent.pipeName} (attempt 1).`);
    } finally {
      host.unregister();
    }
  } finally {
    await ext.close();
    if (previousPipe === undefined) delete process.env.ANCHOR_WITNESS_PIPE;
    else process.env.ANCHOR_WITNESS_PIPE = previousPipe;
    await agent.close();
  }
});
