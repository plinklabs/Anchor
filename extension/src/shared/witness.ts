// Native-messaging witness link to the on-box FocusAgent (#146 part 1).
//
// Soft-enforcement posture (design §5.4): the extension cannot witness its own
// disablement/removal, and the agent cannot witness an InPrivate window it has
// no incognito access to. So the two on-box components witness *each other*.
// This module is the extension half of that link:
//
//   extension --connectNative--> [browser launches] anchor-witness-host.exe
//                                            |  (named pipe)
//                                            v
//                                       FocusAgent  (the on-box witness)
//
// While the native port is open the agent sees a live pipe; when the extension
// is disabled/removed the browser tears the host down, the pipe drops, and the
// agent reports `extension_disabled`. Conversely, the host relays an
// `agent_unavailable` message when *its* pipe to the agent drops (the agent
// died), which we turn into an `agent_unavailable` tamper report here.
//
// Same split as tamper.ts / host-matcher.ts: the pure classification + backoff
// helpers live here and are unit-tested; the chrome.runtime wiring is injected
// so the WitnessClient can be driven with a fake port in tests.

import { logger } from './logger';

const log = logger('witness');

/**
 * Reverse-DNS native-messaging host name. Must match the `name` in the host
 * manifest and the HKCU registration key
 * (`…\Edge\NativeMessagingHosts\net.anchor.witness`).
 */
export const WITNESS_HOST_NAME = 'net.anchor.witness';

/**
 * Keepalive cadence over the native port. A connected native port keeps the
 * MV3 service worker alive, so this timer runs reliably while the link is up.
 * Pings let the agent distinguish a live-but-idle witness from a wedged one;
 * we ping well inside the agent's own staleness window.
 */
export const WITNESS_PING_INTERVAL_MS = 15_000;

/** A message relayed by the native host to the extension. */
export interface WitnessHostMessage {
  type?: string;
}

/** What a host message means for the agent-side witness link. */
export type WitnessHostSignal = 'agent_unavailable' | 'agent_available' | 'ignore';

/**
 * Classify a message the native host relays. The host emits `agent_unavailable`
 * when its pipe to the running FocusAgent drops (the agent died or hasn't
 * started) and `agent_available` when it (re)connects. Everything else —
 * including a malformed or typeless message — is ignored rather than treated as
 * a signal, so a noisy host can't spam tamper reports.
 */
export function classifyHostMessage(msg: WitnessHostMessage | null | undefined): WitnessHostSignal {
  switch (msg?.type) {
    case 'agent_unavailable':
      return 'agent_unavailable';
    case 'agent_available':
      return 'agent_available';
    default:
      return 'ignore';
  }
}

/**
 * Exponential backoff (with a ceiling) for reconnecting the native port. When
 * the host is missing entirely — the agent isn't installed, or its HKCU
 * registration is gone — every `connectNative` fails immediately, so capping
 * the retry rate keeps a misconfigured machine from spinning. `attempt` is
 * zero-based: 1s, 2s, 4s, … up to 60s.
 */
export function nextReconnectDelayMs(attempt: number): number {
  const baseMs = 1_000;
  const maxMs = 60_000;
  return Math.min(maxMs, baseMs * 2 ** Math.max(0, attempt));
}

/** The structural subset of `chrome.runtime.Port` the client uses. */
export interface RuntimePort {
  postMessage(message: unknown): void;
  disconnect(): void;
  onMessage: { addListener(callback: (message: unknown) => void): void };
  onDisconnect: { addListener(callback: () => void): void };
}

export interface WitnessClientDeps {
  /** Opens the native port, e.g. `() => chrome.runtime.connectNative(WITNESS_HOST_NAME)`. */
  connect: () => RuntimePort;
  /** Invoked when the host reports the agent went away (caller decides whether to report tamper). */
  onAgentUnavailable: () => void;
  /** Invoked when the host reports the agent came back. */
  onAgentAvailable?: () => void;
  /** Injected for tests; defaults to the globals. */
  setTimeoutFn?: (callback: () => void, ms: number) => number;
  clearTimeoutFn?: (handle: number) => void;
  pingIntervalMs?: number;
}

/**
 * Maintains the extension→agent native-messaging link: opens the port, keeps it
 * alive with periodic pings, classifies host messages, and reconnects with
 * backoff when the port drops. Stateful and chrome-coupled at the edges, but
 * every chrome dependency is injected so it runs headless in tests.
 */
export class WitnessClient {
  private readonly connect: () => RuntimePort;
  private readonly onAgentUnavailable: () => void;
  private readonly onAgentAvailable?: () => void;
  private readonly setTimeoutFn: (callback: () => void, ms: number) => number;
  private readonly clearTimeoutFn: (handle: number) => void;
  private readonly pingIntervalMs: number;

  private port: RuntimePort | null = null;
  private reconnectAttempt = 0;
  private pingTimer: number | null = null;
  private reconnectTimer: number | null = null;
  private stopped = false;

  constructor(deps: WitnessClientDeps) {
    this.connect = deps.connect;
    this.onAgentUnavailable = deps.onAgentUnavailable;
    this.onAgentAvailable = deps.onAgentAvailable;
    this.setTimeoutFn = deps.setTimeoutFn ?? ((cb, ms) => setTimeout(cb, ms) as unknown as number);
    this.clearTimeoutFn = deps.clearTimeoutFn ?? ((h) => clearTimeout(h));
    this.pingIntervalMs = deps.pingIntervalMs ?? WITNESS_PING_INTERVAL_MS;
  }

  /** Opens the link. Idempotent: a second call while connected is a no-op. */
  start(): void {
    this.stopped = false;
    this.open();
  }

  /** Tears the link down for good (e.g. on session end / SW shutdown). */
  stop(): void {
    this.stopped = true;
    this.clearTimers();
    const port = this.port;
    this.port = null;
    try {
      port?.disconnect();
    } catch (err) {
      log.debug('port disconnect threw on stop', err);
    }
  }

  private open(): void {
    if (this.stopped || this.port) return;

    let port: RuntimePort;
    try {
      port = this.connect();
    } catch (err) {
      // connectNative throws synchronously when the host isn't registered.
      log.warn('connectNative failed — agent witness host not reachable', err);
      this.scheduleReconnect();
      return;
    }

    this.port = port;
    port.onMessage.addListener((message) => this.handleMessage(message));
    port.onDisconnect.addListener(() => this.handleDisconnect());
    log.info('witness port connected to agent host');
    this.reconnectAttempt = 0;
    this.armPing();
  }

  private handleMessage(message: unknown): void {
    const signal = classifyHostMessage(message as WitnessHostMessage);
    if (signal === 'agent_unavailable') {
      log.warn('agent witness reported unavailable');
      this.onAgentUnavailable();
    } else if (signal === 'agent_available') {
      log.info('agent witness reported available');
      this.onAgentAvailable?.();
    }
  }

  private handleDisconnect(): void {
    log.warn('witness port disconnected');
    this.port = null;
    this.clearPing();
    this.scheduleReconnect();
  }

  private scheduleReconnect(): void {
    if (this.stopped || this.reconnectTimer !== null) return;
    const delay = nextReconnectDelayMs(this.reconnectAttempt);
    this.reconnectAttempt += 1;
    this.reconnectTimer = this.setTimeoutFn(() => {
      this.reconnectTimer = null;
      this.open();
    }, delay);
  }

  private armPing(): void {
    this.clearPing();
    this.pingTimer = this.setTimeoutFn(() => {
      this.pingTimer = null;
      this.sendPing();
    }, this.pingIntervalMs);
  }

  private sendPing(): void {
    if (!this.port) return;
    try {
      this.port.postMessage({ type: 'ping' });
    } catch (err) {
      // A throw here means the port is already gone; onDisconnect will follow.
      log.debug('witness ping failed', err);
      return;
    }
    this.armPing();
  }

  private clearPing(): void {
    if (this.pingTimer !== null) {
      this.clearTimeoutFn(this.pingTimer);
      this.pingTimer = null;
    }
  }

  private clearTimers(): void {
    this.clearPing();
    if (this.reconnectTimer !== null) {
      this.clearTimeoutFn(this.reconnectTimer);
      this.reconnectTimer = null;
    }
  }
}
