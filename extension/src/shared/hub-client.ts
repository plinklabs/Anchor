// Thin wrapper around @microsoft/signalr that knows how to:
//   - build the hub URL (with the dev impersonation query string when set)
//   - attach a real Entra access token via SignalR's accessTokenFactory (#289)
//   - call JoinSession after auth succeeds
//   - surface SessionStarted / SessionEnded as plain callbacks
//   - say when the connection is (re)established, and ask whether the student
//     is still in a session, so a SessionEnded missed while offline is caught (#354),
//     and which session started, so a missed SessionStarted is caught too (#356)
//   - keep the connection coming back for as long as the worker runs (#374)
//   - report a BlockedUrl event back to the backend
//
// Keeping the SignalR API surface contained here means background.ts stays
// readable. The two auth modes are mutually exclusive: in dev the impersonation
// OID rides the query string; in production an accessTokenFactory supplies a real
// token (SignalR appends it as ?access_token= for WebSocket/SSE, which is what the
// backend's JwtBearer OnMessageReceived reads on the hub path).

import * as signalR from '@microsoft/signalr';
import { logger } from './logger';
import type { ExtensionSettings } from './settings';
import type {
  AllowlistAmendedPayload,
  BlockedUrlPayload,
  SessionBundlesUpdatedPayload,
  SessionStartedPayload,
  TamperDetectedPayload,
  UnblockRequestPayload,
} from './types';

const log = logger('hub-client');

const HUB_PATH = '/hubs/session';

/** The longest the hub connection waits between attempts to come back (#374). */
export const HUB_RETRY_MAX_DELAY_MS = 30_000;

/**
 * How long to wait before the next attempt to get the hub connection back, given
 * how many attempts have failed since it was lost (#374): 1 s, doubling, at most
 * 30 s apart, and never giving up. That is the agent's SignalRBackoffPolicy with
 * its default cap, and the dashboard's SessionHubRetryPolicy (#370).
 *
 * SignalR's default policy retried at 0, 2, 10 and 30 s and then closed the
 * connection for good. After about 42 s offline (a backend restart, a dropped
 * network, a sleeping laptop) the extension stopped hearing SessionStarted and
 * SessionEnded until the browser restarted, since the witness port keeps the
 * service worker, and so its dead HubClient, alive.
 */
export function hubRetryDelayMs(previousRetryCount: number): number {
  const doublings = Math.min(Math.max(0, previousRetryCount), 10);
  return Math.min(HUB_RETRY_MAX_DELAY_MS, 1_000 * 2 ** doublings);
}

const HUB_RETRY_POLICY: signalR.IRetryPolicy = {
  nextRetryDelayInMilliseconds: (context) => hubRetryDelayMs(context.previousRetryCount),
};

export interface HubCallbacks {
  onSessionStarted: (payload: SessionStartedPayload) => void | Promise<void>;
  onSessionEnded: (sessionId: string) => void | Promise<void>;
  onAllowlistAmended: (payload: AllowlistAmendedPayload) => void | Promise<void>;
  onSessionBundlesUpdated: (payload: SessionBundlesUpdatedPayload) => void | Promise<void>;
  /** After every successful connect: the first start, each automatic
   *  reconnect, and each new start after the connection closed or the first
   *  start failed (#374). Each is a new SignalR connection, which never
   *  receives what the backend broadcast before it existed (#354), nor what it
   *  broadcasts in the moment before it has added the connection to the
   *  student's user group: a connect completes first (#356). */
  onConnected: () => void | Promise<void>;
}

/**
 * What a test puts under the real SignalR client (#374): the HTTP client the
 * negotiate request goes through and the WebSocket it then opens, so it can
 * take the backend away and bring it back. Production passes none.
 */
export interface HubTransportOverrides {
  httpClient?: signalR.HttpClient;
  WebSocket?: unknown;
}

export class HubClient {
  private readonly connection: signalR.HubConnection;
  private readonly settings: ExtensionSettings;
  private readonly callbacks: HubCallbacks;
  /** Set by stop(): the connection was closed on purpose, so nothing restarts it. */
  private stopped = false;
  /** The pending attempt to start the closed connection again, if any. */
  private restartTimer: ReturnType<typeof setTimeout> | null = null;
  /** Starts that failed since the connection was last up, for the backoff. */
  private failedStarts = 0;

  /**
   * @param accessTokenFactory Production auth (#289): returns a fresh Entra access
   *   token for the hub. SignalR calls it on connect and every reconnect, so it
   *   doubles as the refresh hook. Omitted in dev, where the impersonation OID on
   *   the query string authenticates instead.
   * @param transportOverrides For tests only, see HubTransportOverrides.
   */
  constructor(
    settings: ExtensionSettings,
    callbacks: HubCallbacks,
    accessTokenFactory?: () => Promise<string>,
    transportOverrides: HubTransportOverrides = {},
  ) {
    this.settings = settings;
    this.callbacks = callbacks;
    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(this.buildHubUrl(), {
        // The extension service worker is fetch-only — XHR isn't available
        // — so explicitly disable the LongPolling transport's XHR fallback
        // and prefer WebSockets / SSE.
        transport: signalR.HttpTransportType.WebSockets
          | signalR.HttpTransportType.ServerSentEvents,
        // In production, supply a real Entra token; SignalR appends it as
        // ?access_token= for the WebSocket/SSE transports. Mutually exclusive with
        // the dev_impersonate_oid query string (see buildHubUrl).
        ...(accessTokenFactory ? { accessTokenFactory } : {}),
        ...transportOverrides,
      })
      .withAutomaticReconnect(HUB_RETRY_POLICY)
      .configureLogging(signalR.LogLevel.Information)
      .build();

    this.connection.on('SessionStarted', async (payload: SessionStartedPayload) => {
      log.info('SessionStarted received', {
        sessionId: payload.sessionId,
        domainCount: payload.domains?.length ?? 0,
      });
      await this.joinStartedSession(payload);
    });

    this.connection.on('SessionEnded', async (sessionId: string) => {
      log.info('SessionEnded received', { sessionId });
      await callbacks.onSessionEnded(sessionId);
    });

    this.connection.on('AllowlistAmended', async (payload: AllowlistAmendedPayload) => {
      log.info('AllowlistAmended received', {
        sessionId: payload.sessionId,
        addedCount: payload.addedDomains?.length ?? 0,
      });
      await callbacks.onAllowlistAmended(payload);
    });

    this.connection.on('SessionBundlesUpdated', async (payload: SessionBundlesUpdatedPayload) => {
      log.info('SessionBundlesUpdated received', {
        sessionId: payload.sessionId,
        domainCount: payload.domains?.length ?? 0,
      });
      await callbacks.onSessionBundlesUpdated(payload);
    });

    this.connection.onreconnecting((err) => log.warn('reconnecting', err));
    this.connection.onreconnected((id) => {
      log.info('reconnected', { connectionId: id });
      this.notifyConnected();
    });
    // SignalR's own reconnect never gives up now, so the connection closes only
    // when stop() closed it, or when the server turned it away with a close
    // message that allows no reconnect. In that second case nothing else would
    // ever start it again: ensureHub() keeps this client as long as it exists.
    this.connection.onclose((err) => {
      if (this.stopped) return;
      log.warn('connection closed', err);
      this.scheduleRestart();
    });
  }

  /**
   * Connects. If this first start fails, the client goes on trying, on the
   * same backoff, until it connects or stop() is called (#374): SignalR's
   * automatic reconnect only takes over a connection that has been up. Rejects
   * when this first attempt fails.
   */
  async start(): Promise<void> {
    this.stopped = false;
    log.info('starting hub connection', { hubUrl: redactQuery(this.buildHubUrl()) });
    try {
      await this.connection.start();
    } catch (err) {
      this.scheduleRestart();
      throw err;
    }
    this.connected();
  }

  /** Closes the connection for good: no reconnect, no new start. */
  async stop(): Promise<void> {
    this.stopped = true;
    if (this.restartTimer !== null) {
      clearTimeout(this.restartTimer);
      this.restartTimer = null;
    }
    await this.connection.stop();
  }

  // A plain timer, and nothing to keep the service worker up for it: while the
  // worker lives (the witness port holds it), the attempts go on; when Edge
  // suspends it, the timer goes with the HubClient, and the next worker's
  // ensureHub() starts a new connection, which catches up the same way.
  private scheduleRestart(): void {
    if (this.stopped || this.restartTimer !== null) return;
    const delayMs = hubRetryDelayMs(this.failedStarts++);
    log.info('starting the hub connection again', { inMs: delayMs });
    this.restartTimer = setTimeout(() => {
      this.restartTimer = null;
      void this.restart();
    }, delayMs);
  }

  private async restart(): Promise<void> {
    if (this.stopped || this.connection.state !== signalR.HubConnectionState.Disconnected) return;
    try {
      await this.connection.start();
    } catch (err) {
      log.warn('hub connection start failed', err);
      this.scheduleRestart();
      return;
    }
    this.connected();
  }

  private connected(): void {
    this.failedStarts = 0;
    log.info('hub connection established');
    this.notifyConnected();
  }

  /**
   * Whether the student is still in the session: it hasn't ended and they
   * haven't left it (#354). Rejects when the backend can't be asked, e.g. the
   * hub isn't connected.
   */
  async isInSession(sessionId: string): Promise<boolean> {
    return this.connection.invoke<boolean>('IsInSession', sessionId);
  }

  /**
   * The running session the student has been asked into and hasn't declined or
   * left, as the SessionStarted payload its start sent, or null (#356). Rejects
   * when the backend can't be asked.
   */
  async getStartedSession(): Promise<SessionStartedPayload | null> {
    return (await this.connection.invoke<SessionStartedPayload | null>('GetStartedSession')) ?? null;
  }

  /**
   * Joins a session that started and hands it to onSessionStarted: what a
   * SessionStarted broadcast does, and what catching up on a missed one does.
   */
  async joinStartedSession(payload: SessionStartedPayload): Promise<void> {
    try {
      // JoinSession marks the student joined (creating the participant row
      // for a join-by-code student), which ReportEvent (BlockedUrl) and
      // ExtensionHeartbeat require. SessionEnded doesn't depend on it: the
      // backend sends that to the student's user group (#354).
      await this.connection.invoke('JoinSession', {
        sessionId: payload.sessionId,
        joinCode: payload.joinCode,
      });
      log.info('joined session', { sessionId: payload.sessionId });
    } catch (err) {
      log.error('JoinSession failed', err);
    }
    await this.callbacks.onSessionStarted(payload);
  }

  private notifyConnected(): void {
    // Not awaited: whatever the callback does must not hold up start() or
    // SignalR's reconnect handling.
    void Promise.resolve()
      .then(() => this.callbacks.onConnected())
      .catch((err) => log.error('onConnected failed', err));
  }

  /**
   * Posts a BlockedUrl event to the backend. Best-effort: if the hub isn't
   * connected (transient disconnect, no active session) the event is dropped
   * with a warning rather than retried — re-blocking the same URL after
   * reconnect would be noisier than useful.
   */
  async reportBlockedUrl(sessionId: string, payload: BlockedUrlPayload): Promise<void> {
    if (this.connection.state !== signalR.HubConnectionState.Connected) {
      log.warn('reportBlockedUrl skipped — hub not connected', { state: this.connection.state });
      return;
    }
    try {
      await this.connection.invoke('ReportEvent', {
        sessionId,
        kind: 'BlockedUrl',
        payloadJson: JSON.stringify(payload),
        occurredAt: payload.occurredAt,
      });
    } catch (err) {
      log.error('ReportEvent(BlockedUrl) failed', err);
    }
  }

  /**
   * Posts an UnblockRequest event to the backend (#73). Surfaces failures to
   * the caller so the block page can fall back to a "couldn't reach teacher"
   * UI state — unlike BlockedUrl, the student is actively waiting on this
   * call and silent drops would look like the request vanished.
   */
  async reportUnblockRequest(sessionId: string, payload: UnblockRequestPayload): Promise<void> {
    if (this.connection.state !== signalR.HubConnectionState.Connected) {
      throw new Error(`Hub not connected (state: ${this.connection.state})`);
    }
    await this.connection.invoke('ReportEvent', {
      sessionId,
      kind: 'UnblockRequest',
      payloadJson: JSON.stringify(payload),
      occurredAt: new Date().toISOString(),
    });
  }

  /**
   * Reports a TamperDetected event to the backend (#105). Best-effort like
   * reportBlockedUrl: a tamper signal only matters live, so if the hub isn't
   * connected we drop it with a warning rather than queue a stale report.
   */
  async reportTamper(sessionId: string, payload: TamperDetectedPayload): Promise<void> {
    if (this.connection.state !== signalR.HubConnectionState.Connected) {
      log.warn('reportTamper skipped — hub not connected', {
        state: this.connection.state,
        kind: payload.kind,
      });
      return;
    }
    try {
      await this.connection.invoke('ReportEvent', {
        sessionId,
        kind: 'TamperDetected',
        payloadJson: JSON.stringify(payload),
        occurredAt: new Date().toISOString(),
      });
    } catch (err) {
      log.error('ReportEvent(TamperDetected) failed', err);
    }
  }

  /**
   * Sends an extension liveness heartbeat to the backend (#149). Best-effort
   * like reportBlockedUrl: if the hub isn't connected we drop it silently —
   * sustained silence is exactly what the backend's absence-net watches for, so
   * a dropped ping needs no retry. The backend records it under a separate
   * witness source, so it never masks the agent's own HeartbeatLost.
   */
  async sendExtensionHeartbeat(sessionId: string): Promise<void> {
    if (this.connection.state !== signalR.HubConnectionState.Connected) {
      return;
    }
    try {
      await this.connection.invoke('ExtensionHeartbeat', sessionId);
    } catch (err) {
      log.debug('ExtensionHeartbeat failed', err);
    }
  }

  private buildHubUrl(): string {
    const base = this.settings.backendUrl + HUB_PATH;
    if (!this.settings.devImpersonateOid) return base;
    // dev_impersonate_oid is the dev-only auth fallback the backend honours
    // on the hub path (Anchor.Api.Auth.DevImpersonationAuthHandler + #72).
    return `${base}?dev_impersonate_oid=${encodeURIComponent(this.settings.devImpersonateOid)}`;
  }
}

function redactQuery(url: string): string {
  const q = url.indexOf('?');
  return q < 0 ? url : url.slice(0, q) + '?…';
}
