// Configuration the extension reads on startup. Stored in chrome.storage.local
// so it survives service-worker restarts but stays per-profile.
//
// The backend URL is *config, not identity* (#204): a single published
// extension serves every fork, so the URL is NOT baked in as a prod default.
// The on-box agent hands it down at runtime over the native-messaging witness
// link (see witness.ts → persistBackendUrl), which is the canonical source for
// any real deployment. We keep only a dev default (the local backend port) so
// local loops and the headless e2e have something to talk to before the agent
// pushes a URL; for dev you can also set it manually from the background SW
// devtools console:
//
//   chrome.storage.local.set({
//     backendUrl: 'http://localhost:5276',
//     devImpersonateOid: '22222222-2222-2222-2222-222222222222'
//   })
//
// devImpersonateOid is a dev-only shortcut: in Development the backend
// (#72) accepts a dev_impersonate_oid query parameter on the hub URL in
// place of a real Entra token. In production the extension instead acquires a
// real Entra access token via chrome.identity.launchWebAuthFlow (#289) using the
// authConfig the agent hands down over the witness link — config, not identity,
// exactly like backendUrl, so one published extension serves every fork.

import { logger } from './logger';

const log = logger('settings');

/**
 * Per-deployment Entra config the agent hands down over the witness link (#289),
 * used to acquire a student access token for the hub via launchWebAuthFlow. NOT
 * baked into the published extension — a single listing serves every fork, so the
 * tenant/client/scope are learned from the on-box agent at runtime (same posture
 * as backendUrl, #204).
 */
export interface AuthConfig {
  /** Entra tenant (directory) id the sign-in authority targets. */
  tenantId: string;
  /** Public-client app registration id the extension authenticates as. */
  clientId: string;
  /** API scope to request, e.g. `api://<api-app-id>/access_as_user`. */
  scope: string;
}

export interface ExtensionSettings {
  /** Base URL of the Anchor backend, no trailing slash. */
  backendUrl: string;
  /**
   * Dev-only impersonation OID. When set, the extension passes it as the
   * dev_impersonate_oid query parameter on the SignalR hub URL — the dev
   * authentication shortcut, which takes precedence over real auth.
   */
  devImpersonateOid: string | null;
  /**
   * Production Entra auth config (#289). When set (and no devImpersonateOid),
   * the extension acquires a real student token via launchWebAuthFlow and sends
   * it as the hub's access_token. Null until the agent hands it down — the
   * extension then refuses to connect rather than spin in a 401 loop.
   */
  authConfig: AuthConfig | null;
}

/**
 * Dev-only fallback backend URL for local loops before the agent pushes one
 * (#204). NOT a production default — a published fork's extension learns its
 * real backend from the agent at runtime. This is just the local dev port
 * (memory: reference_agent_dashboard_backend_ports).
 */
export const DEV_FALLBACK_BACKEND_URL = 'http://localhost:5276';

const DEFAULTS: ExtensionSettings = {
  backendUrl: DEV_FALLBACK_BACKEND_URL,
  devImpersonateOid: null,
  authConfig: null,
};

/** chrome.storage.local key the agent-pushed backend URL is persisted under. */
export const BACKEND_URL_KEY = 'backendUrl';

/** chrome.storage.local key the agent-pushed auth config is persisted under (#289). */
export const AUTH_CONFIG_KEY = 'authConfig';

const STORAGE_KEYS: (keyof ExtensionSettings)[] = ['backendUrl', 'devImpersonateOid', 'authConfig'];

export async function loadSettings(): Promise<ExtensionSettings> {
  const stored = await chrome.storage.local.get(STORAGE_KEYS);
  const merged: ExtensionSettings = {
    backendUrl: trimTrailingSlash(stringOr(stored.backendUrl, DEFAULTS.backendUrl)),
    devImpersonateOid: nonEmptyOr(stored.devImpersonateOid, DEFAULTS.devImpersonateOid),
    authConfig: parseAuthConfig(stored.authConfig),
  };
  log.debug('settings loaded', {
    backendUrl: merged.backendUrl,
    hasImpersonateOid: merged.devImpersonateOid !== null,
    hasAuthConfig: merged.authConfig !== null,
  });
  return merged;
}

/**
 * Persist a backend URL the agent handed down over the witness link (#204).
 * Returns true if the stored value changed (the caller may need to reconnect
 * the hub onto the new backend). A blank/whitespace URL is rejected so a
 * malformed host message can't wipe a working configuration.
 */
export async function persistBackendUrl(url: string): Promise<boolean> {
  const next = trimTrailingSlash(url.trim());
  if (next.length === 0) {
    log.warn('ignoring empty backend url from agent');
    return false;
  }
  const stored = await chrome.storage.local.get(BACKEND_URL_KEY);
  const current = typeof stored.backendUrl === 'string' ? trimTrailingSlash(stored.backendUrl) : null;
  if (current === next) {
    log.debug('agent backend url unchanged', { url: next });
    return false;
  }
  await chrome.storage.local.set({ [BACKEND_URL_KEY]: next });
  log.info('stored agent-provided backend url', { url: next });
  return true;
}

/**
 * Persist an auth config the agent handed down over the witness link (#289).
 * Returns true if the stored value changed (the caller reconnects the hub onto
 * the new config). A config missing any of tenant/client/scope is rejected so a
 * malformed host message can't half-configure auth and wedge sign-in.
 */
export async function persistAuthConfig(config: AuthConfig): Promise<boolean> {
  const next = normalizeAuthConfig(config);
  if (next === null) {
    log.warn('ignoring incomplete auth config from agent');
    return false;
  }
  const stored = await chrome.storage.local.get(AUTH_CONFIG_KEY);
  const current = parseAuthConfig(stored.authConfig);
  if (current && authConfigsEqual(current, next)) {
    log.debug('agent auth config unchanged');
    return false;
  }
  await chrome.storage.local.set({ [AUTH_CONFIG_KEY]: next });
  log.info('stored agent-provided auth config', { tenantId: next.tenantId, clientId: next.clientId });
  return true;
}

/**
 * How the extension should authenticate to the hub, given its settings (#289):
 *   - `dev`   — a devImpersonateOid is set; use the dev_impersonate_oid shortcut.
 *               Takes precedence so a dev box / e2e harness never needs real auth.
 *   - `token` — no dev oid but an authConfig is present; acquire a real Entra
 *               access token and send it as access_token.
 *   - `none`  — neither is configured; the extension can't authenticate yet
 *               (the agent hasn't handed down config) and must refuse to connect.
 */
export type HubAuthMode = 'dev' | 'token' | 'none';

export function resolveAuthMode(settings: ExtensionSettings): HubAuthMode {
  if (settings.devImpersonateOid) return 'dev';
  if (settings.authConfig) return 'token';
  return 'none';
}

/** Parse + validate a stored/raw auth config; null unless all fields are present. */
function parseAuthConfig(value: unknown): AuthConfig | null {
  if (typeof value !== 'object' || value === null) return null;
  const v = value as Record<string, unknown>;
  return normalizeAuthConfig({
    tenantId: typeof v.tenantId === 'string' ? v.tenantId : '',
    clientId: typeof v.clientId === 'string' ? v.clientId : '',
    scope: typeof v.scope === 'string' ? v.scope : '',
  });
}

function normalizeAuthConfig(config: AuthConfig): AuthConfig | null {
  const tenantId = config.tenantId?.trim() ?? '';
  const clientId = config.clientId?.trim() ?? '';
  const scope = config.scope?.trim() ?? '';
  if (!tenantId || !clientId || !scope) return null;
  return { tenantId, clientId, scope };
}

function authConfigsEqual(a: AuthConfig, b: AuthConfig): boolean {
  return a.tenantId === b.tenantId && a.clientId === b.clientId && a.scope === b.scope;
}

function stringOr(value: unknown, fallback: string): string {
  return typeof value === 'string' && value.length > 0 ? value : fallback;
}

function nonEmptyOr(value: unknown, fallback: string | null): string | null {
  if (typeof value !== 'string') return fallback;
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : fallback;
}

function trimTrailingSlash(url: string): string {
  return url.endsWith('/') ? url.slice(0, -1) : url;
}
