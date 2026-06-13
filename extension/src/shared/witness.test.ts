import { describe, it, expect, vi } from 'vitest';
import {
  classifyHostMessage,
  nextReconnectDelayMs,
  WitnessClient,
  type RuntimePort,
} from './witness';

describe('classifyHostMessage', () => {
  it('maps agent_unavailable / agent_available to their signals', () => {
    expect(classifyHostMessage({ type: 'agent_unavailable' })).toBe('agent_unavailable');
    expect(classifyHostMessage({ type: 'agent_available' })).toBe('agent_available');
  });

  it('ignores unknown, missing, or malformed messages', () => {
    expect(classifyHostMessage({ type: 'ping' })).toBe('ignore');
    expect(classifyHostMessage({})).toBe('ignore');
    expect(classifyHostMessage(null)).toBe('ignore');
    expect(classifyHostMessage(undefined)).toBe('ignore');
  });
});

describe('nextReconnectDelayMs', () => {
  it('doubles from 1s and clamps at 60s', () => {
    expect(nextReconnectDelayMs(0)).toBe(1_000);
    expect(nextReconnectDelayMs(1)).toBe(2_000);
    expect(nextReconnectDelayMs(3)).toBe(8_000);
    expect(nextReconnectDelayMs(6)).toBe(60_000);
    expect(nextReconnectDelayMs(100)).toBe(60_000);
  });

  it('treats negative attempts as the base delay', () => {
    expect(nextReconnectDelayMs(-5)).toBe(1_000);
  });
});

/** A controllable fake of chrome.runtime.Port plus a manual timer queue. */
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
  /** Fire every pending timer whose delay matches, FIFO. Returns count fired. */
  const runDue = (ms: number): number => {
    const due = [...timers.entries()].filter(([, t]) => t.ms === ms);
    for (const [handle, t] of due) {
      timers.delete(handle);
      t.cb();
    }
    return due.length;
  };
  const pendingDelays = (): number[] => [...timers.values()].map((t) => t.ms);

  const ports: FakePort[] = [];
  class FakePort implements RuntimePort {
    posted: unknown[] = [];
    private messageCbs: ((m: unknown) => void)[] = [];
    private disconnectCbs: (() => void)[] = [];
    disconnected = false;
    onMessage = { addListener: (cb: (m: unknown) => void) => this.messageCbs.push(cb) };
    onDisconnect = { addListener: (cb: () => void) => this.disconnectCbs.push(cb) };
    postMessage(m: unknown): void {
      if (this.disconnected) throw new Error('port closed');
      this.posted.push(m);
    }
    disconnect(): void {
      this.disconnected = true;
    }
    emit(m: unknown): void {
      this.messageCbs.forEach((cb) => cb(m));
    }
    drop(): void {
      this.disconnected = true;
      this.disconnectCbs.forEach((cb) => cb());
    }
  }

  return { setTimeoutFn, clearTimeoutFn, runDue, pendingDelays, ports, FakePort };
}

describe('WitnessClient', () => {
  it('pings the agent host on the keepalive interval while connected', () => {
    const h = makeHarness();
    const port = new h.FakePort();
    const client = new WitnessClient({
      connect: () => port,
      onAgentUnavailable: vi.fn(),
      setTimeoutFn: h.setTimeoutFn,
      clearTimeoutFn: h.clearTimeoutFn,
      pingIntervalMs: 15_000,
    });

    client.start();
    expect(h.runDue(15_000)).toBe(1);
    expect(port.posted).toEqual([{ type: 'ping' }]);
    // The ping re-arms itself for the next interval.
    expect(h.pendingDelays()).toContain(15_000);
  });

  it('reports the agent unavailable when the host relays it', () => {
    const h = makeHarness();
    const port = new h.FakePort();
    const onAgentUnavailable = vi.fn();
    const onAgentAvailable = vi.fn();
    const client = new WitnessClient({
      connect: () => port,
      onAgentUnavailable,
      onAgentAvailable,
      setTimeoutFn: h.setTimeoutFn,
      clearTimeoutFn: h.clearTimeoutFn,
    });

    client.start();
    port.emit({ type: 'agent_unavailable' });
    expect(onAgentUnavailable).toHaveBeenCalledTimes(1);

    port.emit({ type: 'agent_available' });
    expect(onAgentAvailable).toHaveBeenCalledTimes(1);

    // An unrelated message is not a signal.
    port.emit({ type: 'whatever' });
    expect(onAgentUnavailable).toHaveBeenCalledTimes(1);
  });

  it('reconnects with backoff after the port drops', () => {
    const h = makeHarness();
    const ports = [new h.FakePort(), new h.FakePort()];
    let i = 0;
    const client = new WitnessClient({
      connect: () => ports[i++],
      onAgentUnavailable: vi.fn(),
      setTimeoutFn: h.setTimeoutFn,
      clearTimeoutFn: h.clearTimeoutFn,
    });

    client.start();
    ports[0].drop();
    // First reconnect is scheduled at the 1s base delay.
    expect(h.pendingDelays()).toContain(1_000);
    expect(h.runDue(1_000)).toBe(1);
    // Reconnected onto the second port — a ping arms against it.
    expect(i).toBe(2);
    expect(h.runDue(15_000)).toBe(1);
    expect(ports[1].posted).toEqual([{ type: 'ping' }]);
  });

  it('schedules a reconnect when connectNative throws (host not registered)', () => {
    const h = makeHarness();
    const client = new WitnessClient({
      connect: () => {
        throw new Error('Specified native messaging host not found.');
      },
      onAgentUnavailable: vi.fn(),
      setTimeoutFn: h.setTimeoutFn,
      clearTimeoutFn: h.clearTimeoutFn,
    });

    client.start();
    expect(h.pendingDelays()).toContain(1_000);
  });

  it('stop() disconnects the port and cancels all timers', () => {
    const h = makeHarness();
    const port = new h.FakePort();
    const client = new WitnessClient({
      connect: () => port,
      onAgentUnavailable: vi.fn(),
      setTimeoutFn: h.setTimeoutFn,
      clearTimeoutFn: h.clearTimeoutFn,
    });

    client.start();
    client.stop();
    expect(port.disconnected).toBe(true);
    expect(h.pendingDelays()).toEqual([]);
    // A drop after stop must not schedule another reconnect.
    port.drop();
    expect(h.pendingDelays()).toEqual([]);
  });
});
