// #572: the agent link keeps retrying after Edge stopped the service worker.
//
// Runs the real background.ts against an in-memory browser, one import per
// worker generation: stopping the worker drops its listeners and its pending
// timers, as Edge does, while what the browser keeps (chrome.alarms,
// chrome.storage.session) survives into the next generation.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { LINK_RETRY_ALARM } from './shared/link-retry';

type Listener = (...args: any[]) => unknown;

function fakeEvent() {
  const listeners: Listener[] = [];
  return {
    listeners,
    addListener: (l: Listener) => void listeners.push(l),
    removeListener: (l: Listener) => {
      const i = listeners.indexOf(l);
      if (i >= 0) listeners.splice(i, 1);
    },
    hasListener: (l: Listener) => listeners.includes(l),
  };
}

class FakePort {
  readonly messageListeners: Listener[] = [];
  readonly disconnectListeners: Listener[] = [];
  open = true;
  onMessage = { addListener: (l: Listener) => void this.messageListeners.push(l) };
  onDisconnect = { addListener: (l: Listener) => void this.disconnectListeners.push(l) };
  postMessage(): void {
    if (!this.open) throw new Error('Attempting to use a disconnected port object');
  }
  disconnect(): void {
    this.open = false;
  }
  emit(message: unknown): void {
    this.messageListeners.forEach((l) => l(message));
  }
  drop(): void {
    this.open = false;
    this.disconnectListeners.forEach((l) => l());
  }
}

/** The browser around the worker: what outlives a worker generation. */
class FakeBrowser {
  /** Whether the witness host can be started (false: e.g. mid-reinstall). */
  hostUp = false;
  connectAttempts = 0;
  readonly ports: FakePort[] = [];
  readonly alarms = new Map<string, { delayInMinutes?: number; periodInMinutes?: number }>();
  readonly sessionStore: Record<string, unknown> = {};
  generations = 0;
  private onAlarm: ReturnType<typeof fakeEvent> | null = null;

  get workerRunning(): boolean {
    return this.onAlarm !== null;
  }

  /** Start a worker generation: a fresh `chrome` and background.js top level. */
  async startWorker(): Promise<void> {
    const onAlarm = fakeEvent();
    this.onAlarm = onAlarm;
    this.generations += 1;
    vi.stubGlobal('chrome', this.chromeFor(onAlarm));
    vi.resetModules();
    await import('./background');
    await vi.advanceTimersByTimeAsync(0);
  }

  /** Edge stops an idle worker: its listeners and its timers go with it. */
  stopWorker(): void {
    this.onAlarm = null;
    vi.clearAllTimers();
  }

  /**
   * An alarm comes due: a stopped worker is started for it first, then the
   * alarm is dispatched to it. One that doesn't exist never fires.
   */
  async fireAlarm(name: string): Promise<boolean> {
    if (!this.alarms.has(name)) return false;
    if (!this.onAlarm) await this.startWorker();
    this.onAlarm!.listeners.forEach((l) => l({ name, scheduledTime: Date.now() }));
    await vi.advanceTimersByTimeAsync(0);
    return true;
  }

  get openPort(): FakePort | undefined {
    return this.ports.find((p) => p.open);
  }

  private connectNative(): FakePort {
    this.connectAttempts += 1;
    const port = new FakePort();
    this.ports.push(port);
    // As Edge does it: a host that can't start hands back a port that drops a
    // moment later (#243); a live one relays the agent's state on connect.
    queueMicrotask(() => {
      if (!this.hostUp) port.drop();
      else {
        port.emit({ type: 'agent_available' });
        port.emit({ type: 'no_session' });
      }
    });
    return port;
  }

  private chromeFor(onAlarm: ReturnType<typeof fakeEvent>) {
    const store = this.sessionStore;
    const alarms = this.alarms;
    return {
      runtime: {
        id: 'anchor-test',
        getURL: (path: string) => `chrome-extension://anchor-test/${path}`,
        connectNative: () => this.connectNative(),
        sendMessage: async () => undefined,
        onInstalled: fakeEvent(),
        onStartup: fakeEvent(),
        onMessage: fakeEvent(),
      },
      alarms: {
        onAlarm,
        get: async (name: string) => (alarms.has(name) ? { name, ...alarms.get(name) } : undefined),
        create: async (name: string, info: { delayInMinutes?: number; periodInMinutes?: number }) => {
          alarms.set(name, info);
        },
        clear: async (name: string) => alarms.delete(name),
      },
      storage: {
        session: {
          get: async (key: string) => ({ [key]: store[key] }),
          set: async (items: Record<string, unknown>) => void Object.assign(store, items),
          remove: async (key: string) => void delete store[key],
        },
      },
      webNavigation: { onBeforeNavigate: fakeEvent() },
      tabs: {
        onUpdated: fakeEvent(),
        query: async () => [],
        update: async () => undefined,
        create: async () => ({}),
      },
      permissions: { onAdded: fakeEvent(), onRemoved: fakeEvent(), contains: async () => true },
      extension: { isAllowedIncognitoAccess: async () => true },
      windows: { update: async () => undefined },
    };
  }
}

describe('the agent link while the service worker is stopped (#572)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    for (const level of ['debug', 'info', 'warn', 'error'] as const) {
      vi.spyOn(console, level).mockImplementation(() => {});
    }
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('an alarm brings the link back once the reconnect delay is past 30 s and the worker was stopped', async () => {
    const browser = new FakeBrowser();
    await browser.startWorker();
    expect(browser.connectAttempts).toBe(1);

    // The host can't be started; the worker retries at 1, 2, 4, 8, 16 s…
    await vi.advanceTimersByTimeAsync(1_000 + 2_000 + 4_000 + 8_000 + 16_000);
    expect(browser.connectAttempts).toBe(6);
    // …and the next try is 32 s out: past the worker's idle timeout.

    // Edge stops the idle worker, and the planned retry goes with it. The host
    // can be started again, but nothing the student does wakes the worker.
    browser.stopWorker();
    browser.hostUp = true;
    await vi.advanceTimersByTimeAsync(5 * 60_000);
    expect(browser.connectAttempts).toBe(6);
    expect(browser.openPort).toBeUndefined();

    // The retry alarm wakes it, and the link is back.
    expect(browser.alarms.get(LINK_RETRY_ALARM)).toEqual({ delayInMinutes: 0.5, periodInMinutes: 0.5 });
    expect(await browser.fireAlarm(LINK_RETRY_ALARM)).toBe(true);
    expect(browser.generations).toBe(2);
    expect(browser.connectAttempts).toBe(7);
    expect(browser.openPort).toBeDefined();
    // Up again: no more wake-ups.
    expect(browser.alarms.has(LINK_RETRY_ALARM)).toBe(false);
  });

  it('in a worker still awake, the alarm cuts a long reconnect delay short', async () => {
    const browser = new FakeBrowser();
    await browser.startWorker();
    // Tries at 0, 1, 3, 7, 15, 31 and 63 s; the next is 60 s out.
    await vi.advanceTimersByTimeAsync(63_000);
    expect(browser.connectAttempts).toBe(7);

    browser.hostUp = true;
    expect(await browser.fireAlarm(LINK_RETRY_ALARM)).toBe(true);
    expect(browser.generations).toBe(1);
    expect(browser.connectAttempts).toBe(8);
    expect(browser.openPort).toBeDefined();
    expect(browser.alarms.has(LINK_RETRY_ALARM)).toBe(false);
  });

  it('a link that comes up at once leaves no alarm behind', async () => {
    const browser = new FakeBrowser();
    browser.hostUp = true;
    await browser.startWorker();
    expect(browser.openPort).toBeDefined();
    expect(browser.alarms.size).toBe(0);
  });
});
