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
  mode: string;
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
  mode: string;
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
