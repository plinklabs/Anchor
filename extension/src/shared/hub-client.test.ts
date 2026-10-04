// #374 — the hub connection never gives up, and every connect catches up.
//
// These run the real @microsoft/signalr client inside HubClient against an
// in-memory hub (a fake negotiate endpoint and a fake WebSocket speaking the
// JSON hub protocol), in fake time, so an outage of minutes takes no time and
// the retry schedule can be read off exactly. SignalR's default policy retried
// at 0, 2, 10 and 30 s and then closed the connection for good, and nothing
// started a closed connection again, nor one whose first start failed: the
// extension went deaf to SessionStarted and SessionEnded until the browser
// restarted.
//
// Each client is wired the way background.ts wires it: onConnected runs the
// catch-up (#354, #356) through the client's own IsInSession and
// GetStartedSession calls, so what reaches the fake hub is what the backend
// would see.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import * as signalR from '@microsoft/signalr';
import { HUB_RETRY_MAX_DELAY_MS, HubClient, hubRetryDelayMs } from './hub-client';
import { catchUpOnConnect } from './session-confirm';
import type { SessionStartedPayload } from './types';

const RECORD_SEPARATOR = '\x1e';

interface Invocation {
  target: string;
  args: unknown[];
}

interface FakeCloseEvent {
  code: number;
  reason: string;
  wasClean: boolean;
}

/**
 * One connection's WebSocket, as SignalR sees it. The hub decides whether it
 * opens; frames the client sends go to the hub, and the hub's replies arrive as
 * messages a microtask later, as they would from a socket.
 */
class FakeSocket {
  static readonly OPEN = 1;
  readyState = 0;
  binaryType = '';
  onopen: ((event: unknown) => void) | null = null;
  onmessage: ((event: { data: string }) => void) | null = null;
  onclose: ((event: FakeCloseEvent) => void) | null = null;
  onerror: ((event: unknown) => void) | null = null;
  handshakeDone = false;
  private readonly hub: FakeHub;

  constructor(hub: FakeHub) {
    this.hub = hub;
    queueMicrotask(() => {
      if (!hub.up) {
        this.readyState = 3;
        this.onclose?.({ code: 1006, reason: '', wasClean: false });
        return;
      }
      this.readyState = FakeSocket.OPEN;
      hub.sockets.add(this);
      this.onopen?.({});
    });
  }

  send(data: string): void {
    for (const frame of data.split(RECORD_SEPARATOR).filter((f) => f.length > 0)) {
      this.hub.receive(this, JSON.parse(frame));
    }
  }

  close(): void {
    this.readyState = 3;
    this.hub.sockets.delete(this);
  }

  deliver(message: unknown): void {
    queueMicrotask(() => {
      if (this.readyState !== FakeSocket.OPEN) return;
      this.onmessage?.({ data: JSON.stringify(message) + RECORD_SEPARATOR });
    });
  }
}

/**
 * An in-memory SignalR hub: answers negotiate, accepts WebSocket connections,
 * completes the JSON-protocol handshake, answers invocations from `handlers`,
 * and can broadcast, drop every connection, turn connections away, or be
 * unreachable altogether.
 */
class FakeHub {
  /** Whether the backend can be reached. While false, negotiate fails the way
   *  fetch does on a dead network. */
  up = true;
  /** The fake-clock time of every connect attempt (negotiate request), whether
   *  it got through or not. */
  readonly attempts: number[] = [];
  readonly invocations: Invocation[] = [];
  handlers: Record<string, (...args: unknown[]) => unknown> = {
    IsInSession: () => true,
    GetStartedSession: () => null,
    JoinSession: () => null,
  };
  /** The open connections. */
  readonly sockets = new Set<FakeSocket>();

  /** What HubClient negotiates through, and the WebSocket it opens. */
  readonly httpClient: signalR.HttpClient;
  readonly WebSocket: unknown;

  constructor() {
    const hub = this;
    this.httpClient = new (class extends signalR.HttpClient {
      async send(): Promise<signalR.HttpResponse> {
        hub.attempts.push(Date.now());
        if (!hub.up) throw new TypeError('Failed to fetch');
        const n = hub.attempts.length;
        return new signalR.HttpResponse(
          200,
          'OK',
          JSON.stringify({
            negotiateVersion: 1,
            connectionId: `connection-${n}`,
            connectionToken: `token-${n}`,
            availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
          }),
        );
      }
    })();
    this.WebSocket = class extends FakeSocket {
      constructor() {
        super(hub);
      }
    };
  }

  /** The arguments of every call to `target`, in order. */
  calls(target: string): unknown[][] {
    return this.invocations.filter((i) => i.target === target).map((i) => i.args);
  }

  /** Sends a hub method call to every open connection, as a broadcast does. */
  broadcast(target: string, ...args: unknown[]): void {
    for (const socket of this.sockets) socket.deliver({ type: 1, target, arguments: args });
  }

  /** The network goes away: every open connection drops, as on a reset, and
   *  nothing gets through until `up` is set again. */
  goDown(): void {
    this.up = false;
    for (const socket of [...this.sockets]) {
      socket.close();
      socket.onclose?.({ code: 1006, reason: '', wasClean: false });
    }
  }

  /** The server turns every open connection away: a close message that
   *  doesn't allow a reconnect, so SignalR's own reconnect doesn't run. */
  turnAway(): void {
    for (const socket of this.sockets) {
      socket.deliver({ type: 7, error: 'Connection closed with an error.' });
    }
  }

  receive(
    socket: FakeSocket,
    message: { type?: number; target?: string; arguments?: unknown[]; invocationId?: string },
  ): void {
    if (!socket.handshakeDone) {
      socket.handshakeDone = true;
      socket.deliver({});
      return;
    }
    switch (message.type) {
      case 1: {
        const target = message.target ?? '';
        const args = message.arguments ?? [];
        this.invocations.push({ target, args });
        if (message.invocationId !== undefined) {
          const result = this.handlers[target]?.(...args) ?? null;
          socket.deliver({ type: 3, invocationId: message.invocationId, result });
        }
        return;
      }
      case 6:
        // Answer the client's keepalive ping so its server timeout never trips.
        socket.deliver({ type: 6 });
        return;
      default:
        return;
    }
  }
}

function startedPayload(sessionId: string): SessionStartedPayload {
  return {
    sessionId,
    classId: 'class-3a',
    startedAt: '2026-10-01T08:00:00Z',
    joinCode: `${sessionId}-code`,
    apps: [],
    domains: [{ matchType: 'Exact', value: 'smartschool.be' }],
  };
}

/**
 * A HubClient on `hub`, wired like background.ts: an in-memory active session,
 * SessionStarted/SessionEnded handlers that set and clear it, and the catch-up
 * on every connect.
 */
function makeClient(hub: FakeHub, activeSessionId: string | null) {
  const state = { activeSessionId, started: [] as string[], ended: [] as string[] };
  const onSessionEnded = async (sessionId: string): Promise<void> => {
    state.ended.push(sessionId);
    if (state.activeSessionId === sessionId) state.activeSessionId = null;
  };
  const client: HubClient = new HubClient(
    { backendUrl: 'http://backend.test', devImpersonateOid: 'student-oid', authConfig: null },
    {
      onSessionStarted: async (payload) => {
        state.started.push(payload.sessionId);
        state.activeSessionId = payload.sessionId;
      },
      onSessionEnded,
      onAllowlistAmended: async () => {},
      onSessionBundlesUpdated: async () => {},
      onConnected: () =>
        catchUpOnConnect({
          getActiveSessionId: async () => state.activeSessionId,
          isInSession: (sessionId) => client.isInSession(sessionId),
          endSession: onSessionEnded,
          getStartedSession: () => client.getStartedSession(),
          startSession: (payload) => client.joinStartedSession(payload),
        }),
    },
    undefined,
    { httpClient: hub.httpClient, WebSocket: hub.WebSocket },
  );
  return { client, state };
}

/** Lets every promise chain that isn't waiting on a timer run to the end: the
 *  fake hub works in microtasks, which a real macrotask boundary drains. */
async function settle(): Promise<void> {
  await vi.advanceTimersByTimeAsync(0);
  await new Promise<void>((resolve) => setImmediate(resolve));
}

/** Advances fake time, letting everything that becomes due run. */
async function elapse(ms: number): Promise<void> {
  await vi.advanceTimersByTimeAsync(ms);
  await settle();
}

/** The gaps between `from` and each attempt, and between attempts. */
function gaps(attempts: number[], from: number): number[] {
  return attempts.map((at, i) => at - (i === 0 ? from : attempts[i - 1]));
}

/** What the retry schedule looks like from its start: 1, 2, 4, 8, 16 s, then
 *  every 30 s. */
function expectedGaps(count: number): number[] {
  return Array.from({ length: count }, (_, i) => Math.min(30_000, 1_000 * 2 ** i));
}

const TEN_MINUTES = 10 * 60_000;
/** When SignalR's default policy gave up: attempts at 0, 2, 12 and 42 s. */
const DEFAULT_POLICY_GIVE_UP_MS = 42_000;

describe('hubRetryDelayMs', () => {
  it('backs off from 1 s, doubling, to 30 s, like the agent and the dashboard (#370)', () => {
    expect([0, 1, 2, 3, 4, 5, 6].map(hubRetryDelayMs)).toEqual([
      1_000, 2_000, 4_000, 8_000, 16_000, 30_000, 30_000,
    ]);
    expect(HUB_RETRY_MAX_DELAY_MS).toBe(30_000);
  });

  it('never gives up, however many attempts failed', () => {
    for (const failed of [10, 100, 10_000, Number.MAX_SAFE_INTEGER]) {
      expect(hubRetryDelayMs(failed)).toBe(30_000);
    }
  });
});

describe('HubClient (#374)', () => {
  beforeEach(() => {
    // setImmediate stays real: settle() uses it to drain the fake hub's
    // microtasks. SignalR only uses setTimeout and Date.
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'] });
    for (const level of ['debug', 'log', 'info', 'warn', 'error'] as const) {
      vi.spyOn(console, level).mockImplementation(() => {});
    }
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('keeps reconnecting through a 10-minute outage, then catches up on the session that ended and the one that started meanwhile', async () => {
    const hub = new FakeHub();
    const { client, state } = makeClient(hub, 'session-a');
    await client.start();
    await settle();
    expect(hub.calls('IsInSession')).toEqual([['session-a']]);

    const downAt = Date.now();
    hub.goDown();
    await elapse(TEN_MINUTES);

    // Reconnect attempts at 1, 2, 4, 8, 16 s apart, then every 30 s, for the
    // whole outage: 23 of them in 10 minutes, 18 after the old give-up point.
    const retries = hub.attempts.slice(1);
    expect(gaps(retries, downAt)).toEqual(expectedGaps(23));
    expect(retries.filter((at) => at - downAt > DEFAULT_POLICY_GIVE_UP_MS)).toHaveLength(18);

    // Meanwhile the teacher ended session A and started session B.
    hub.handlers.IsInSession = (sessionId) => sessionId !== 'session-a';
    hub.handlers.GetStartedSession = () => startedPayload('session-b');
    const before = hub.invocations.length;
    hub.up = true;
    await elapse(HUB_RETRY_MAX_DELAY_MS);

    expect(hub.invocations.slice(before)).toEqual([
      { target: 'IsInSession', args: ['session-a'] },
      { target: 'GetStartedSession', args: [] },
      { target: 'JoinSession', args: [{ sessionId: 'session-b', joinCode: 'session-b-code' }] },
    ]);
    expect(state.ended).toEqual(['session-a']);
    expect(state.started).toEqual(['session-b']);

    // And the connection hears broadcasts again.
    hub.broadcast('SessionEnded', 'session-b');
    await settle();
    expect(state.ended).toEqual(['session-a', 'session-b']);
    expect(state.activeSessionId).toBeNull();

    await client.stop();
  });

  it('starts the connection again after the server turned it away, through an outage, and catches up', async () => {
    const hub = new FakeHub();
    const { client, state } = makeClient(hub, null);
    await client.start();
    await settle();

    const closedAt = Date.now();
    hub.turnAway();
    hub.up = false;
    await settle();
    await elapse(5 * 60_000);

    // 1, 2, 4, 8, 16 s, then every 30 s: 13 attempts in 5 minutes.
    const restarts = hub.attempts.slice(1);
    expect(gaps(restarts, closedAt)).toEqual(expectedGaps(13));

    hub.handlers.GetStartedSession = () => startedPayload('session-b');
    hub.up = true;
    await elapse(HUB_RETRY_MAX_DELAY_MS);

    expect(hub.calls('GetStartedSession')).toHaveLength(2);
    expect(hub.calls('JoinSession')).toEqual([[{ sessionId: 'session-b', joinCode: 'session-b-code' }]]);
    expect(state.started).toEqual(['session-b']);

    hub.broadcast('SessionEnded', 'session-b');
    await settle();
    expect(state.ended).toEqual(['session-b']);

    await client.stop();
  });

  it('keeps trying when its first start fails, and catches up once it connects', async () => {
    const hub = new FakeHub();
    hub.up = false;
    const { client, state } = makeClient(hub, 'session-a');

    const startedAt = Date.now();
    await expect(client.start()).rejects.toThrow();
    await elapse(5 * 60_000);

    // The failed start, then 1, 2, 4, 8, 16 s, then every 30 s.
    expect(hub.attempts[0]).toBe(startedAt);
    expect(gaps(hub.attempts.slice(1), startedAt)).toEqual(expectedGaps(13));

    hub.handlers.IsInSession = () => false;
    hub.handlers.GetStartedSession = () => startedPayload('session-b');
    hub.up = true;
    await elapse(HUB_RETRY_MAX_DELAY_MS);

    expect(hub.calls('IsInSession')).toEqual([['session-a']]);
    expect(hub.calls('GetStartedSession')).toEqual([[]]);
    expect(state.ended).toEqual(['session-a']);
    expect(state.started).toEqual(['session-b']);

    await client.stop();
  });

  it('stops retrying once stopped, whether it was reconnecting or starting again', async () => {
    // Reconnecting after a drop.
    const dropped = new FakeHub();
    const first = makeClient(dropped, null).client;
    await first.start();
    dropped.goDown();
    await elapse(10_000);
    const droppedAttempts = dropped.attempts.length;
    await first.stop();
    await elapse(TEN_MINUTES);
    expect(dropped.attempts).toHaveLength(droppedAttempts);

    // Starting again after a failed first start.
    const unreachable = new FakeHub();
    unreachable.up = false;
    const second = makeClient(unreachable, null).client;
    await expect(second.start()).rejects.toThrow();
    await elapse(10_000);
    const unreachableAttempts = unreachable.attempts.length;
    await second.stop();
    await elapse(TEN_MINUTES);
    expect(unreachable.attempts).toHaveLength(unreachableAttempts);
  });
});
