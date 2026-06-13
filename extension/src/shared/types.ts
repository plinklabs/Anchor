// Wire shapes the extension consumes from the backend, expressed in the
// camelCase form SignalR's JsonHubProtocol produces over the wire. The .NET
// record on the backend is PascalCase (SessionStartedPayload from
// backend/src/Anchor.Api/Realtime/Dtos.cs), but JsonHubProtocol applies a
// camelCase naming policy by default — the agent (also .NET) doesn't notice
// because its client-side deserializer is case-insensitive, but the JS
// client receives the raw camelCase JSON.

import type { AllowedDomain } from './host-matcher';

export interface AllowedAppDto {
  matchKind: string;
  value: string;
}

/**
 * Domain entry on the wire. Shape is identical to the matcher's
 * AllowedDomain (matchType + value), so no field-renaming step is needed
 * between the SignalR payload and the matcher input.
 */
export type AllowedDomainDto = AllowedDomain;

export interface SessionStartedPayload {
  sessionId: string;
  classId: string;
  startedAt: string;
  joinCode: string;
  apps: ReadonlyArray<AllowedAppDto>;
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
 * Payload reported back to the backend each time a navigation is blocked.
 * Maps to EventKind.BlockedUrl on the backend; the JSON shape is documented
 * in issue #72.
 */
export interface BlockedUrlPayload {
  url: string;
  host: string;
  tabId: number;
  occurredAt: string;
}

/**
 * Payload the extension sends to the backend when a student clicks
 * "Request access" on the block page. Maps to EventKind.UnblockRequest on
 * the backend; the wire shape is documented in issue #73.
 */
export interface UnblockRequestPayload {
  url: string;
  host: string;
  reason?: string;
}

/**
 * Delta-shaped allowlist amendment pushed by the backend when a teacher
 * approves a pending unblock request. Mirrors AllowlistAmendedPayload on
 * the backend; pushed only to the granted student's user group (#73).
 */
export interface AllowlistAmendedPayload {
  sessionId: string;
  userId: string;
  addedDomains: ReadonlyArray<AllowedDomainDto>;
}

/**
 * Full allowlist replacement pushed by the backend when a teacher changes the
 * session's bundles mid-session (#93). Targets the student's user group, so the
 * `domains` here already include that student's unblock grants (#73) — the
 * extension replaces its cached domain set rather than merging. `apps` is for
 * the co-located agent; the extension only consumes `domains`.
 */
export interface SessionBundlesUpdatedPayload {
  sessionId: string;
  apps: ReadonlyArray<AllowedAppDto>;
  domains: ReadonlyArray<AllowedDomainDto>;
}

/**
 * The kinds of tamper the extension can witness in-browser (#105, #146):
 * - `inprivate_opened` — an InPrivate window was created during a session.
 * - `host_permission_revoked` — "Site access" was downgraded from "On all
 *   sites", so the navigation filter lost the host access it needs.
 * - `agent_unavailable` — the on-box FocusAgent witness went away mid-session
 *   (#146 part 1): the native-messaging host relays that its pipe to the agent
 *   dropped, so the agent can no longer witness the browser. The mirror signal
 *   (`extension_disabled`, when the extension itself is disabled/removed) is
 *   reported by the *agent* over the same TamperDetected event, not here.
 * Sent as the `kind` field of a TamperDetected event's payload; the backend
 * surfaces it verbatim on the teacher's roster.
 */
export type TamperKind = 'inprivate_opened' | 'host_permission_revoked' | 'agent_unavailable';

/**
 * Payload the extension sends when it observes a tamper attempt during an
 * active session. Maps to EventKind.TamperDetected on the backend (#105).
 */
export interface TamperDetectedPayload {
  kind: TamperKind;
}

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
      kind: 'allowlist-amended';
      sessionId: string;
      addedHosts: ReadonlyArray<string>;
    };
