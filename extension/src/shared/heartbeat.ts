// Hub heartbeat for the extension witness (#149).
//
// The agent already heartbeats the hub so the backend can tell a live agent
// from a dead one. The extension did not — which left a blind spot: when the
// native-messaging witness link (#146) never existed (host not installed) or
// both on-box components vanish (browser closed), nothing told the backend the
// extension was gone. So the extension now pings too, under a separate witness
// source, and the backend's HeartbeatMonitor turns sustained silence into a
// `TamperDetected{extension_silent}` flag.
//
// A connected SignalR hub keeps the MV3 service worker alive, so a plain timer
// runs reliably while the link is up — and if the worker dies (the very thing
// we want to detect), the timer dying with it is correct: the pings stop and
// the backend flags the silence. Same injected-deps shape as WitnessClient so
// it runs headless in tests.

import { logger } from './logger';

const log = logger('heartbeat');

/**
 * Ping cadence. Matches the agent's default 10s interval so both witnesses sit
 * inside the same backend staleness window (2× interval) — see
 * `HeartbeatOptions` on the backend.
 */
export const EXTENSION_HEARTBEAT_INTERVAL_MS = 10_000;

export interface SessionHeartbeatDeps {
  /** Sends one heartbeat for the given session, e.g. `hubClient.sendExtensionHeartbeat`. */
  sendHeartbeat: (sessionId: string) => Promise<void> | void;
  /** Resolves the active session id, or null when no session is enforcing. */
  getActiveSessionId: () => Promise<string | null> | string | null;
  /** Injected for tests; defaults to the globals. */
  setTimeoutFn?: (callback: () => void, ms: number) => number;
  clearTimeoutFn?: (handle: number) => void;
  intervalMs?: number;
}

/**
 * Drives a periodic hub heartbeat while a session is active. Each tick resolves
 * the current session and pings only when one is enforcing — outside a session
 * there is nothing to witness, so the timer keeps ticking but sends nothing.
 * The loop re-arms itself (rather than using a repeating interval) so a slow
 * send can't pile ticks up on top of each other.
 */
export class SessionHeartbeat {
  private readonly sendHeartbeat: (sessionId: string) => Promise<void> | void;
  private readonly getActiveSessionId: () => Promise<string | null> | string | null;
  private readonly setTimeoutFn: (callback: () => void, ms: number) => number;
  private readonly clearTimeoutFn: (handle: number) => void;
  private readonly intervalMs: number;

  private timer: number | null = null;
  private stopped = false;

  constructor(deps: SessionHeartbeatDeps) {
    this.sendHeartbeat = deps.sendHeartbeat;
    this.getActiveSessionId = deps.getActiveSessionId;
    this.setTimeoutFn = deps.setTimeoutFn ?? ((cb, ms) => setTimeout(cb, ms) as unknown as number);
    this.clearTimeoutFn = deps.clearTimeoutFn ?? ((h) => clearTimeout(h));
    this.intervalMs = deps.intervalMs ?? EXTENSION_HEARTBEAT_INTERVAL_MS;
  }

  /** Starts the loop. Idempotent: a second call while running is a no-op. */
  start(): void {
    this.stopped = false;
    if (this.timer !== null) return;
    this.arm();
  }

  /** Stops the loop and cancels the pending tick. */
  stop(): void {
    this.stopped = true;
    if (this.timer !== null) {
      this.clearTimeoutFn(this.timer);
      this.timer = null;
    }
  }

  private arm(): void {
    if (this.stopped) return;
    this.timer = this.setTimeoutFn(() => {
      this.timer = null;
      void this.tick();
    }, this.intervalMs);
  }

  private async tick(): Promise<void> {
    try {
      const sessionId = await this.getActiveSessionId();
      if (sessionId) {
        await this.sendHeartbeat(sessionId);
      }
    } catch (err) {
      // Best-effort: a failed ping is one missed sample, not a reason to stop.
      // Sustained failure is itself the silence the backend is watching for.
      log.debug('extension heartbeat tick failed', err);
    } finally {
      this.arm();
    }
  }
}
