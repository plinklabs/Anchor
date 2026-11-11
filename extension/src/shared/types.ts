// Wire shapes for the session the extension enforces. Since #489 they reach the
// extension from the on-box agent over the witness link (see witness.ts), not
// from the backend: the agent receives them from the backend (SignalR's
// JsonHubProtocol, camelCase), keeps the session it joined, and hands it down in
// the same camelCase shape. The .NET records on the backend are PascalCase
// (backend/src/Anchor.Api/Realtime/Dtos.cs).

import type { AllowedDomain } from './host-matcher';

/**
 * Domain entry on the wire. Shape is identical to the matcher's
 * AllowedDomain (matchType + value), so no field-renaming step is needed
 * between the agent's message and the matcher input.
 */
export type AllowedDomainDto = AllowedDomain;

/**
 * The session the agent joined, as it hands it down (`session_started`, #489):
 * the backend's SessionStarted payload without the apps, which only the agent
 * enforces.
 */
export interface SessionStartedPayload {
  sessionId: string;
  classId: string;
  startedAt: string;
  joinCode: string;
  domains: ReadonlyArray<AllowedDomainDto>;
}

/**
 * The shape the extension caches in chrome.storage.session — a compact view
 * of the active session derived from SessionStartedPayload.
 */
export interface ActiveSessionState {
  sessionId: string;
  classId: string;
  joinCode: string;
  startedAt: string;
  domains: ReadonlyArray<AllowedDomain>;
}

/**
 * Payload reported each time a navigation is blocked; the agent sends it on as
 * EventKind.BlockedUrl (#72, #489).
 */
export interface BlockedUrlPayload {
  url: string;
  host: string;
  tabId: number;
  occurredAt: string;
}

/**
 * What the student asks for with "Request access" on the block page; the agent
 * sends it on as EventKind.UnblockRequest (#73, #489).
 */
export interface UnblockRequestPayload {
  url: string;
  host: string;
  reason?: string;
}

/**
 * The kinds of tamper the extension can witness in-browser (#105):
 * - `inprivate_opened` — an InPrivate window was created during a session.
 * - `host_permission_revoked` — "Site access" was downgraded from "On all
 *   sites", so the navigation filter lost the host access it needs.
 * The agent sends them on as the `kind` of a TamperDetected event (#489), which
 * the backend surfaces verbatim on the teacher's roster. What the extension
 * can't witness about itself (`extension_disabled`) the agent reports.
 */
export type TamperKind = 'inprivate_opened' | 'host_permission_revoked';

/**
 * Wire format for chrome.runtime messages exchanged between the background
 * service worker and the block page. Kept in one discriminated union so a
 * recipient can switch on `kind` without speculating about the shape.
 */
export type ExtensionRuntimeMessage =
  | {
      kind: 'unblock-request';
      sessionId: string;
      payload: UnblockRequestPayload;
    }
  | {
      /**
       * The session's allowlist changed: a teacher approved an unblock request
       * (#73) or changed the session's bundles (#426). Open block pages use it
       * to send the student back to a page it now covers.
       */
      kind: 'allowlist-amended';
      sessionId: string;
      /** The full allowlist now in force, entries as the matcher reads them. */
      domains: ReadonlyArray<AllowedDomainDto>;
    };
