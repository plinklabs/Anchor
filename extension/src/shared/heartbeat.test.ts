import { describe, it, expect } from 'vitest';
import { SessionHeartbeat } from './heartbeat';

/** Manual timer queue plus a microtask flush, mirroring witness.test's harness.
 *  SessionHeartbeat.tick is async, so after firing a timer we flush a real
 *  macrotask to let the awaited deps settle and the loop re-arm. */
function makeHarness() {
  let nextHandle = 1;
  const timers = new Map<number, { cb: () => void; ms: number }>();
  const setTimeoutFn = (cb: () => void, ms: number): number => {
    const handle = nextHandle++;
    timers.set(handle, { cb, ms });
    return handle;
  };
  const clearTimeoutFn = (handle: number): void => {
    timers.delete(handle);
  };
  const runDue = (ms: number): number => {
    const due = [...timers.entries()].filter(([, t]) => t.ms === ms);
    for (const [handle, t] of due) {
      timers.delete(handle);
      t.cb();
    }
    return due.length;
  };
  const pendingDelays = (): number[] => [...timers.values()].map((t) => t.ms);
  const flush = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, 0));
  return { setTimeoutFn, clearTimeoutFn, runDue, pendingDelays, flush };
}

describe('SessionHeartbeat', () => {
  it('pings the active session on each interval and re-arms itself', async () => {
    const h = makeHarness();
    const sent: string[] = [];
    const hb = new SessionHeartbeat({
      sendHeartbeat: (id) => {
        sent.push(id);
      },
      getActiveSessionId: () => 'sess-1',
      setTimeoutFn: h.setTimeoutFn,
      clearTimeoutFn: h.clearTimeoutFn,
      intervalMs: 10_000,
    });

    hb.start();
    expect(h.pendingDelays()).toEqual([10_000]);

    expect(h.runDue(10_000)).toBe(1);
    await h.flush();
    expect(sent).toEqual(['sess-1']);
    // The tick re-arms for the next interval.
    expect(h.pendingDelays()).toEqual([10_000]);

    expect(h.runDue(10_000)).toBe(1);
    await h.flush();
    expect(sent).toEqual(['sess-1', 'sess-1']);
  });

  it('keeps ticking but sends nothing while no session is active', async () => {
    const h = makeHarness();
    const sent: string[] = [];
    const hb = new SessionHeartbeat({
      sendHeartbeat: (id) => {
        sent.push(id);
      },
      getActiveSessionId: () => null,
      setTimeoutFn: h.setTimeoutFn,
      clearTimeoutFn: h.clearTimeoutFn,
      intervalMs: 10_000,
    });

    hb.start();
    h.runDue(10_000);
    await h.flush();

    expect(sent).toEqual([]);
    // Still armed — a session may start later in this worker's lifetime.
    expect(h.pendingDelays()).toEqual([10_000]);
  });

  it('survives a failing send and re-arms for the next interval', async () => {
    const h = makeHarness();
    const hb = new SessionHeartbeat({
      sendHeartbeat: () => {
        throw new Error('hub not connected');
      },
      getActiveSessionId: () => 'sess-1',
      setTimeoutFn: h.setTimeoutFn,
      clearTimeoutFn: h.clearTimeoutFn,
      intervalMs: 10_000,
    });

    hb.start();
    h.runDue(10_000);
    await h.flush();

    // A thrown send is one missed sample, not a reason to stop pinging.
    expect(h.pendingDelays()).toEqual([10_000]);
  });

  it('start() is idempotent — a second call does not stack timers', () => {
    const h = makeHarness();
    const hb = new SessionHeartbeat({
      sendHeartbeat: () => {},
      getActiveSessionId: () => 'sess-1',
      setTimeoutFn: h.setTimeoutFn,
      clearTimeoutFn: h.clearTimeoutFn,
      intervalMs: 10_000,
    });

    hb.start();
    hb.start();
    expect(h.pendingDelays()).toEqual([10_000]);
  });

  it('stop() cancels the pending tick and stays stopped', async () => {
    const h = makeHarness();
    const sent: string[] = [];
    const hb = new SessionHeartbeat({
      sendHeartbeat: (id) => {
        sent.push(id);
      },
      getActiveSessionId: () => 'sess-1',
      setTimeoutFn: h.setTimeoutFn,
      clearTimeoutFn: h.clearTimeoutFn,
      intervalMs: 10_000,
    });

    hb.start();
    hb.stop();
    expect(h.pendingDelays()).toEqual([]);

    // A stray timer firing after stop must not resurrect the loop.
    h.runDue(10_000);
    await h.flush();
    expect(sent).toEqual([]);
  });
});
