// Thin wrapper around @microsoft/signalr that knows how to:
//   - build the hub URL (with the dev impersonation query string when set)
//   - call JoinSession after auth succeeds
//   - surface SessionStarted / SessionEnded as plain callbacks
//   - report a BlockedUrl event back to the backend
//
// Keeping the SignalR API surface contained here means background.ts stays
// readable and the auth-mode swap (dev impersonation → real Entra token)
// only touches one file.

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

export interface HubCallbacks {
  onSessionStarted: (payload: SessionStartedPayload) => void | Promise<void>;
  onSessionEnded: (sessionId: string) => void | Promise<void>;
  onAllowlistAmended: (payload: AllowlistAmendedPayload) => void | Promise<void>;
  onSessionBundlesUpdated: (payload: SessionBundlesUpdatedPayload) => void | Promise<void>;
}

export class HubClient {
  private readonly connection: signalR.HubConnection;
  private readonly settings: ExtensionSettings;

  constructor(settings: ExtensionSettings, callbacks: HubCallbacks) {
    this.settings = settings;
    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(this.buildHubUrl(), {
        // The extension service worker is fetch-only — XHR isn't available
        // — so explicitly disable the LongPolling transport's XHR fallback
        // and prefer WebSockets / SSE.
        transport: signalR.HttpTransportType.WebSockets
          | signalR.HttpTransportType.ServerSentEvents,
      })
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Information)
      .build();

    this.connection.on('SessionStarted', async (payload: SessionStartedPayload) => {
      log.info('SessionStarted received', {
        sessionId: payload.sessionId,
        domainCount: payload.domains?.length ?? 0,
      });
      try {
        // The hub broadcasts SessionEnded to the SESSION group, so the
        // extension must JoinSession to subscribe. JoinSession also creates
        // the SessionParticipant row that ReportEvent (BlockedUrl) requires.
        await this.connection.invoke('JoinSession', {
          sessionId: payload.sessionId,
          joinCode: payload.joinCode,
        });
        log.info('joined session group', { sessionId: payload.sessionId });
      } catch (err) {
        log.error('JoinSession failed', err);
      }
      await callbacks.onSessionStarted(payload);
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
    this.connection.onreconnected((id) => log.info('reconnected', { connectionId: id }));
    this.connection.onclose((err) => log.warn('connection closed', err));
  }

  async start(): Promise<void> {
    log.info('starting hub connection', { hubUrl: redactQuery(this.buildHubUrl()) });
    await this.connection.start();
    log.info('hub connection established');
  }

  async stop(): Promise<void> {
    await this.connection.stop();
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
