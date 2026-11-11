// The native-messaging link to the on-box FocusAgent (#146 part 1, #489).
//
//   extension --connectNative--> [browser launches] anchor-witness-host.exe
//                                            |  (named pipe)
//                                            v
//                                       FocusAgent
//
// Two jobs ride on it:
//
// - The agent witnesses the extension (#146). While the native port is open the
//   agent sees a live pipe; when the extension is disabled or removed the browser
//   tears the host down, the pipe drops, and the agent reports
//   `extension_disabled` and pauses Edge (#427).
// - The agent is the extension's only identity (#489). The extension has no
//   sign-in and no backend connection: the agent, signed in with the Windows
//   account, hands the session it joined down this link (`session_started`, the
//   allowlist changes, `session_ended`, and the current state on every connect),
//   and the extension hands back what it reports (blocked URLs, unblock requests,
//   its in-browser tamper signals), which the agent sends with its own connection.
//
// Same split as tamper.ts / host-matcher.ts: the pure parsing + backoff helpers
// live here and are unit-tested; the chrome.runtime wiring is injected so the
// WitnessClient can be driven with a fake port in tests. The message names match
// FocusAgent.Core.Extension.ExtensionMessages on the agent side.

import { logger } from './logger';
import type { AllowedDomainDto, SessionStartedPayload } from './types';

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

/** How long the block page's "Request access" waits for the agent's answer. */
export const UNBLOCK_REQUEST_TIMEOUT_MS = 10_000;

/** What the agent tells the extension about the session (#489). */
export type AgentMessage =
  | ({ type: 'session_started' } & SessionStartedPayload)
  | { type: 'no_session' }
  | { type: 'session_ended'; sessionId: string }
  | { type: 'allowlist_replaced'; sessionId: string; domains: AllowedDomainDto[] }
  | { type: 'allowlist_amended'; sessionId: string; addedDomains: AllowedDomainDto[] };

/** The agent's answer to an unblock request, matched by its request id. */
export interface UnblockRequestResult {
  ok: boolean;
  error?: string;
}

/** What the extension sends the agent to report (#489). */
export type ExtensionReport =
  | { type: 'blocked_url'; sessionId: string; url: string; host: string; tabId: number; occurredAt: string }
  | { type: 'tamper'; sessionId: string; kind: string };

/** An unblock request, before the client gives it a request id. */
export interface UnblockRequest {
  sessionId: string;
  url: string;
  host: string;
  reason?: string;
}

/** What a message from the native host means. */
export type HostSignal =
  | { kind: 'agent_available' }
  | { kind: 'agent_unavailable' }
  | { kind: 'agent'; message: AgentMessage }
  | { kind: 'unblock_result'; requestId: string; result: UnblockRequestResult }
  | { kind: 'ignore' };

/**
 * Parse a message the native host relays. The host itself emits
 * `agent_available` / `agent_unavailable` as its pipe to the agent comes and
 * goes; everything else is the agent's. A message that is malformed — a missing
 * session id, domains that aren't a list — is ignored rather than half-applied,
 * so a noisy host can't wipe a working allowlist. So are the `backend_url` and
 * `auth_config` the host still sends for extensions from before #489, which
 * signed in and connected to the backend themselves.
 */
export function parseHostMessage(raw: unknown): HostSignal {
  if (typeof raw !== 'object' || raw === null) return { kind: 'ignore' };
  const msg = raw as Record<string, unknown>;
  switch (msg.type) {
    case 'agent_available':
      return { kind: 'agent_available' };
    case 'agent_unavailable':
      return { kind: 'agent_unavailable' };
    case 'session_started': {
      if (!isNonBlank(msg.sessionId) || !isDomainList(msg.domains)) return { kind: 'ignore' };
      return {
        kind: 'agent',
        message: {
          type: 'session_started',
          sessionId: msg.sessionId,
          classId: stringOr(msg.classId, ''),
          joinCode: stringOr(msg.joinCode, ''),
          startedAt: stringOr(msg.startedAt, ''),
          domains: msg.domains,
        },
      };
    }
    case 'no_session':
      return { kind: 'agent', message: { type: 'no_session' } };
    case 'session_ended':
      return isNonBlank(msg.sessionId)
        ? { kind: 'agent', message: { type: 'session_ended', sessionId: msg.sessionId } }
        : { kind: 'ignore' };
    case 'allowlist_replaced':
      return isNonBlank(msg.sessionId) && isDomainList(msg.domains)
        ? { kind: 'agent', message: { type: 'allowlist_replaced', sessionId: msg.sessionId, domains: msg.domains } }
        : { kind: 'ignore' };
    case 'allowlist_amended':
      return isNonBlank(msg.sessionId) && isDomainList(msg.addedDomains)
        ? {
            kind: 'agent',
            message: { type: 'allowlist_amended', sessionId: msg.sessionId, addedDomains: msg.addedDomains },
          }
        : { kind: 'ignore' };
    case 'unblock_request_result':
      return isNonBlank(msg.requestId)
        ? {
            kind: 'unblock_result',
            requestId: msg.requestId,
            result: {
              ok: msg.ok === true,
              ...(typeof msg.error === 'string' ? { error: msg.error } : {}),
            },
          }
        : { kind: 'ignore' };
    default:
      return { kind: 'ignore' };
  }
}

function isNonBlank(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0;
}

function stringOr(value: unknown, fallback: string): string {
  return typeof value === 'string' ? value : fallback;
}

function isDomainList(value: unknown): value is AllowedDomainDto[] {
  return (
    Array.isArray(value) &&
    value.every(
      (d) =>
        typeof d === 'object' &&
        d !== null &&
        typeof (d as AllowedDomainDto).matchType === 'string' &&
        typeof (d as AllowedDomainDto).value === 'string',
    )
  );
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
  /**
   * Invoked for each message the agent sends about the session (#489), in the
   * order it arrives. Only the registered native host can reach this path, so
   * an arbitrary web page can't start or end a session.
   */
  onAgentMessage: (message: AgentMessage) => void;
  /** Invoked when the host reports the agent went away. */
  onAgentUnavailable?: () => void;
  /** Invoked when the host reports the agent came back. */
  onAgentAvailable?: () => void;
  /** Injected for tests; default to the globals. */
  setTimeoutFn?: (callback: () => void, ms: number) => number;
  clearTimeoutFn?: (handle: number) => void;
  newRequestId?: () => string;
  pingIntervalMs?: number;
  unblockTimeoutMs?: number;
}

interface PendingRequest {
  resolve: (result: UnblockRequestResult) => void;
  reject: (err: Error) => void;
  timer: number;
}

/**
 * Maintains the extension↔agent native-messaging link: opens the port, keeps it
 * alive with periodic pings, parses the agent's messages, posts the extension's
 * reports, and reconnects with backoff when the port drops. Stateful and
 * chrome-coupled at the edges, but every chrome dependency is injected so it
 * runs headless in tests.
 */
export class WitnessClient {
  private readonly connect: () => RuntimePort;
  private readonly onAgentMessage: (message: AgentMessage) => void;
  private readonly onAgentUnavailable?: () => void;
  private readonly onAgentAvailable?: () => void;
  private readonly setTimeoutFn: (callback: () => void, ms: number) => number;
  private readonly clearTimeoutFn: (handle: number) => void;
  private readonly newRequestId: () => string;
  private readonly pingIntervalMs: number;
  private readonly unblockTimeoutMs: number;

  private port: RuntimePort | null = null;
  /**
   * Whether the current port has proven a live host by relaying at least one
   * message. `connectNative` to a *missing* host doesn't throw — it returns a
   * port that drops a moment later (#243) — so port creation alone is not proof
   * of a connection. Only a received message resets the reconnect backoff.
   */
  private established = false;
  /** Whether the host last said the agent behind it is up. */
  private agentUp = false;
  private reconnectAttempt = 0;
  private pingTimer: number | null = null;
  private reconnectTimer: number | null = null;
  private stopped = false;
  private readonly pending = new Map<string, PendingRequest>();

  constructor(deps: WitnessClientDeps) {
    this.connect = deps.connect;
    this.onAgentMessage = deps.onAgentMessage;
    this.onAgentUnavailable = deps.onAgentUnavailable;
    this.onAgentAvailable = deps.onAgentAvailable;
    this.setTimeoutFn = deps.setTimeoutFn ?? ((cb, ms) => setTimeout(cb, ms) as unknown as number);
    this.clearTimeoutFn = deps.clearTimeoutFn ?? ((h) => clearTimeout(h));
    this.newRequestId = deps.newRequestId ?? (() => crypto.randomUUID());
    this.pingIntervalMs = deps.pingIntervalMs ?? WITNESS_PING_INTERVAL_MS;
    this.unblockTimeoutMs = deps.unblockTimeoutMs ?? UNBLOCK_REQUEST_TIMEOUT_MS;
  }

  /** Opens the link. Idempotent: a second call while connected is a no-op. */
  start(): void {
    this.stopped = false;
    this.open();
  }

  /** Tears the link down for good (e.g. on SW shutdown). */
  stop(): void {
    this.stopped = true;
    this.clearTimers();
    const port = this.port;
    this.port = null;
    this.failPending('The Anchor app link was closed.');
    try {
      port?.disconnect();
    } catch (err) {
      log.debug('port disconnect threw on stop', err);
    }
  }

  /**
   * Hands a report to the agent, which sends it with its own connection.
   * Best-effort like the reports always were: returns false, and drops the
   * report, when no agent is connected — a block re-reported later would be
   * noisier than useful, and a tamper signal only matters live.
   */
  report(message: ExtensionReport): boolean {
    // A port the host hasn't spoken on yet is still worth posting to: a worker
    // revived by a navigation blocks that navigation before the host's first
    // message, and the browser hands the post over once the host is up.
    if (!this.port) {
      log.warn('report dropped — the Anchor app is not connected', { type: message.type });
      return false;
    }
    return this.post(message);
  }

  /**
   * Asks the agent to send an unblock request to the teacher, and resolves with
   * its answer. Rejects when the agent isn't connected or doesn't answer in
   * time, so the block page can tell the student the request didn't go out
   * instead of letting it vanish.
   */
  requestUnblock(request: UnblockRequest): Promise<UnblockRequestResult> {
    if (!this.port || !this.established || !this.agentUp) {
      return Promise.reject(new Error('The Anchor app is not connected.'));
    }
    const requestId = this.newRequestId();
    return new Promise<UnblockRequestResult>((resolve, reject) => {
      const timer = this.setTimeoutFn(() => {
        this.pending.delete(requestId);
        reject(new Error('The Anchor app did not answer.'));
      }, this.unblockTimeoutMs);
      this.pending.set(requestId, { resolve, reject, timer });
      if (!this.post({ type: 'unblock_request', requestId, ...request })) {
        this.settle(requestId, null, new Error('The Anchor app is not connected.'));
      }
    });
  }

  private post(message: unknown): boolean {
    try {
      this.port?.postMessage(message);
      return this.port !== null;
    } catch (err) {
      // A throw here means the port is already gone; onDisconnect will follow.
      log.debug('posting to the agent failed', err);
      return false;
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
    this.established = false;
    port.onMessage.addListener((message) => this.handleMessage(message));
    port.onDisconnect.addListener(() => this.handleDisconnect());
    // Don't claim a connection yet: a missing host hands back a port that drops
    // immediately (#243). We confirm the link — and reset the backoff — only
    // once the host actually relays a message (see handleMessage).
    log.debug('witness port opened; awaiting first host message');
    this.armPing();
  }

  private handleMessage(message: unknown): void {
    if (!this.established) {
      // First message over this port is proof of a live host: now reset the
      // reconnect backoff so a healthy agent comes back to the 1s base delay.
      this.established = true;
      this.reconnectAttempt = 0;
      log.info('witness port connected to agent host');
    }
    const signal = parseHostMessage(message);
    switch (signal.kind) {
      case 'agent_unavailable':
        log.warn('agent witness reported unavailable');
        this.agentUp = false;
        this.failPending('The Anchor app went away.');
        this.onAgentUnavailable?.();
        return;
      case 'agent_available':
        log.info('agent witness reported available');
        this.agentUp = true;
        this.onAgentAvailable?.();
        return;
      case 'agent':
        this.agentUp = true;
        log.info(`agent message: ${signal.message.type}`);
        this.onAgentMessage(signal.message);
        return;
      case 'unblock_result':
        this.settle(signal.requestId, signal.result, null);
        return;
      case 'ignore':
        return;
    }
  }

  private settle(requestId: string, result: UnblockRequestResult | null, error: Error | null): void {
    const pending = this.pending.get(requestId);
    if (!pending) return;
    this.pending.delete(requestId);
    this.clearTimeoutFn(pending.timer);
    if (error) pending.reject(error);
    else pending.resolve(result!);
  }

  private failPending(reason: string): void {
    for (const requestId of [...this.pending.keys()]) {
      this.settle(requestId, null, new Error(reason));
    }
  }

  private handleDisconnect(): void {
    if (this.established) {
      // An established link dropping is a real event (agent/browser restart).
      log.info('witness port disconnected');
    } else {
      // A port that drops before any message means the host was never there —
      // the expected, quiet case on a device without the agent (#243).
      log.debug('witness port dropped before any host message (agent likely absent)');
    }
    this.established = false;
    this.agentUp = false;
    this.port = null;
    this.clearPing();
    this.failPending('The Anchor app link dropped.');
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
