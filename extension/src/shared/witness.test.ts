import { describe, it, expect, vi } from 'vitest';
import {
  parseHostMessage,
  nextReconnectDelayMs,
  WitnessClient,
  type RuntimePort,
} from './witness';

const DOMAINS = [{ matchType: 'Wildcard', value: '*.sharepoint.com' }];

describe('parseHostMessage', () => {
  it('maps agent_unavailable / agent_available to their signals', () => {
    expect(parseHostMessage({ type: 'agent_unavailable' })).toEqual({ kind: 'agent_unavailable' });
    expect(parseHostMessage({ type: 'agent_available' })).toEqual({ kind: 'agent_available' });
  });

  it('ignores unknown, missing, or malformed messages', () => {
    expect(parseHostMessage({ type: 'ping' })).toEqual({ kind: 'ignore' });
    expect(parseHostMessage({})).toEqual({ kind: 'ignore' });
    expect(parseHostMessage(null)).toEqual({ kind: 'ignore' });
    expect(parseHostMessage(undefined)).toEqual({ kind: 'ignore' });
    expect(parseHostMessage('session_started')).toEqual({ kind: 'ignore' });
  });

  it('ignores the backend_url / auth_config the host still sends for older extensions (#489)', () => {
    // The extension no longer signs in or connects to the backend itself.
    expect(parseHostMessage({ type: 'backend_url', url: 'https://anchor.example' })).toEqual({ kind: 'ignore' });
    expect(parseHostMessage({ type: 'auth_config', tenantId: 't', clientId: 'c', scope: 's' })).toEqual({
      kind: 'ignore',
    });
  });

  it('parses the session the agent hands down (#489)', () => {
    expect(
      parseHostMessage({
        type: 'session_started',
        sessionId: 's1',
        classId: 'c1',
        joinCode: 'ABC123',
        startedAt: '2026-11-11T08:00:00Z',
        domains: DOMAINS,
      }),
    ).toEqual({
      kind: 'agent',
      message: {
        type: 'session_started',
        sessionId: 's1',
        classId: 'c1',
        joinCode: 'ABC123',
        startedAt: '2026-11-11T08:00:00Z',
        domains: DOMAINS,
      },
    });
    expect(parseHostMessage({ type: 'no_session' })).toEqual({ kind: 'agent', message: { type: 'no_session' } });
    expect(parseHostMessage({ type: 'session_ended', sessionId: 's1' })).toEqual({
      kind: 'agent',
      message: { type: 'session_ended', sessionId: 's1' },
    });
    expect(parseHostMessage({ type: 'allowlist_replaced', sessionId: 's1', domains: [] })).toEqual({
      kind: 'agent',
      message: { type: 'allowlist_replaced', sessionId: 's1', domains: [] },
    });
    expect(parseHostMessage({ type: 'allowlist_amended', sessionId: 's1', addedDomains: DOMAINS })).toEqual({
      kind: 'agent',
      message: { type: 'allowlist_amended', sessionId: 's1', addedDomains: DOMAINS },
    });
  });

  it('ignores a session message it cannot apply whole, so it never wipes a working allowlist', () => {
    expect(parseHostMessage({ type: 'session_started', domains: DOMAINS })).toEqual({ kind: 'ignore' });
    expect(parseHostMessage({ type: 'session_started', sessionId: 's1' })).toEqual({ kind: 'ignore' });
    expect(parseHostMessage({ type: 'session_started', sessionId: 's1', domains: [{ value: 'x' }] })).toEqual({
      kind: 'ignore',
    });
    expect(parseHostMessage({ type: 'session_ended', sessionId: '  ' })).toEqual({ kind: 'ignore' });
    expect(parseHostMessage({ type: 'allowlist_replaced', sessionId: 's1', domains: 'x' })).toEqual({
      kind: 'ignore',
    });
    expect(parseHostMessage({ type: 'allowlist_amended', sessionId: 's1' })).toEqual({ kind: 'ignore' });
  });

  it('parses the answer to an unblock request', () => {
    expect(parseHostMessage({ type: 'unblock_request_result', requestId: 'r1', ok: true })).toEqual({
      kind: 'unblock_result',
      requestId: 'r1',
      result: { ok: true },
    });
    expect(
      parseHostMessage({ type: 'unblock_request_result', requestId: 'r1', ok: false, error: 'hub down' }),
    ).toEqual({ kind: 'unblock_result', requestId: 'r1', result: { ok: false, error: 'hub down' } });
    expect(parseHostMessage({ type: 'unblock_request_result', ok: true })).toEqual({ kind: 'ignore' });
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
      onAgentMessage: vi.fn(),
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
      onAgentMessage: vi.fn(),
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

  it("hands the agent's session messages to onAgentMessage, in order (#489)", () => {
    const h = makeHarness();
    const port = new h.FakePort();
    const onAgentMessage = vi.fn();
    const client = new WitnessClient({
      connect: () => port,
      onAgentMessage,
      setTimeoutFn: h.setTimeoutFn,
      clearTimeoutFn: h.clearTimeoutFn,
    });

    client.start();
    port.emit({ type: 'agent_available' });
    port.emit({ type: 'session_started', sessionId: 's1', domains: DOMAINS });
    port.emit({ type: 'session_ended', sessionId: 's1' });
    // A malformed one never reaches the handler.
    port.emit({ type: 'session_started' });

    expect(onAgentMessage.mock.calls.map(([m]) => m.type)).toEqual(['session_started', 'session_ended']);
  });

  it('posts a report to the agent while the port is open, and drops it when there is none', () => {
    const h = makeHarness();
    const port = new h.FakePort();
    const client = new WitnessClient({
      connect: () => port,
      onAgentMessage: vi.fn(),
      setTimeoutFn: h.setTimeoutFn,
      clearTimeoutFn: h.clearTimeoutFn,
    });
    const report = {
      type: 'blocked_url' as const,
      sessionId: 's1',
      url: 'https://offlist.test/',
      host: 'offlist.test',
      tabId: 4,
      occurredAt: '2026-11-11T08:00:00Z',
    };

    expect(client.report(report)).toBe(false);
    client.start();
    // Even before the host's first message: a worker woken by a navigation
    // reports blocking it before the host has spoken.
    expect(client.report(report)).toBe(true);
    expect(port.posted).toEqual([report]);

    port.drop();
    expect(client.report(report)).toBe(false);
  });

  it("resolves an unblock request with the agent's answer to it", async () => {
    const h = makeHarness();
    const port = new h.FakePort();
    const client = new WitnessClient({
      connect: () => port,
      onAgentMessage: vi.fn(),
      setTimeoutFn: h.setTimeoutFn,
      clearTimeoutFn: h.clearTimeoutFn,
      newRequestId: () => 'r1',
    });
    client.start();
    port.emit({ type: 'agent_available' });

    const answer = client.requestUnblock({ sessionId: 's1', url: 'https://docs.test/', host: 'docs.test' });
    expect(port.posted).toEqual([
      { type: 'unblock_request', requestId: 'r1', sessionId: 's1', url: 'https://docs.test/', host: 'docs.test' },
    ]);
    // An answer to another request is not this one's.
    port.emit({ type: 'unblock_request_result', requestId: 'other', ok: false });
    port.emit({ type: 'unblock_request_result', requestId: 'r1', ok: false, error: 'hub down' });

    await expect(answer).resolves.toEqual({ ok: false, error: 'hub down' });
  });

  it('rejects an unblock request when no agent is connected, answers late, or the link drops', async () => {
    const h = makeHarness();
    const port = new h.FakePort();
    let n = 0;
    const client = new WitnessClient({
      connect: () => port,
      onAgentMessage: vi.fn(),
      setTimeoutFn: h.setTimeoutFn,
      clearTimeoutFn: h.clearTimeoutFn,
      newRequestId: () => `r${++n}`,
      unblockTimeoutMs: 10_000,
    });

    client.start();
    // The host never spoke: there is no agent to send it.
    await expect(client.requestUnblock({ sessionId: 's1', url: 'u', host: 'h' })).rejects.toThrow(/not connected/);

    port.emit({ type: 'agent_available' });
    const late = client.requestUnblock({ sessionId: 's1', url: 'u', host: 'h' });
    expect(h.runDue(10_000)).toBe(1);
    await expect(late).rejects.toThrow(/did not answer/);

    // The agent behind the host goes away: a request waiting on it fails at
    // once, and so does the next one, until the agent is back.
    const waiting = client.requestUnblock({ sessionId: 's1', url: 'u', host: 'h' });
    port.emit({ type: 'agent_unavailable' });
    await expect(waiting).rejects.toThrow(/went away/);
    await expect(client.requestUnblock({ sessionId: 's1', url: 'u', host: 'h' })).rejects.toThrow(/not connected/);

    port.emit({ type: 'agent_available' });
    const dropped = client.requestUnblock({ sessionId: 's1', url: 'u', host: 'h' });
    port.drop();
    await expect(dropped).rejects.toThrow(/dropped/);
  });

  it('reconnects with backoff after the port drops', () => {
    const h = makeHarness();
    const ports = [new h.FakePort(), new h.FakePort()];
    let i = 0;
    const client = new WitnessClient({
      connect: () => ports[i++],
      onAgentMessage: vi.fn(),
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

  it('grows the reconnect backoff when the port drops with no message (#243)', () => {
    // A *missing* native host doesn't throw: connectNative hands back a port
    // that drops a moment later with no message. The backoff must grow each
    // cycle (1s → 2s → 4s …) instead of pinning at 1s, which it did when
    // open() reset the attempt counter on mere port creation.
    const h = makeHarness();
    const ports = [new h.FakePort(), new h.FakePort(), new h.FakePort()];
    let i = 0;
    const client = new WitnessClient({
      connect: () => ports[i++],
      onAgentMessage: vi.fn(),
      setTimeoutFn: h.setTimeoutFn,
      clearTimeoutFn: h.clearTimeoutFn,
    });

    client.start();
    ports[0].drop();
    expect(h.pendingDelays()).toContain(1_000);
    expect(h.runDue(1_000)).toBe(1);

    ports[1].drop();
    expect(h.pendingDelays()).toContain(2_000);
    expect(h.runDue(2_000)).toBe(1);

    ports[2].drop();
    expect(h.pendingDelays()).toContain(4_000);
  });

  it('resets the backoff once the host relays a message (#243)', () => {
    const h = makeHarness();
    const ports = [new h.FakePort(), new h.FakePort(), new h.FakePort()];
    let i = 0;
    const client = new WitnessClient({
      connect: () => ports[i++],
      onAgentMessage: vi.fn(),
      onAgentAvailable: vi.fn(),
      setTimeoutFn: h.setTimeoutFn,
      clearTimeoutFn: h.clearTimeoutFn,
    });

    client.start();
    // Grow the backoff with two message-less drops...
    ports[0].drop();
    expect(h.runDue(1_000)).toBe(1);
    ports[1].drop();
    expect(h.runDue(2_000)).toBe(1);

    // ...now the host proves itself live with a message, then drops again: the
    // next reconnect is back at the 1s base delay, not 4s.
    ports[2].emit({ type: 'agent_available' });
    ports[2].drop();
    expect(h.pendingDelays()).toContain(1_000);
    expect(h.pendingDelays()).not.toContain(4_000);
  });

  it('schedules a reconnect when connectNative throws (host not registered)', () => {
    const h = makeHarness();
    const client = new WitnessClient({
      connect: () => {
        throw new Error('Specified native messaging host not found.');
      },
      onAgentMessage: vi.fn(),
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
      onAgentMessage: vi.fn(),
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
