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
// place of a real Entra token. Production rollout will swap this for a
// chrome.identity.launchWebAuthFlow path — tracked in a follow-up issue.

import { logger } from './logger';

const log = logger('settings');

export interface ExtensionSettings {
  /** Base URL of the Anchor backend, no trailing slash. */
  backendUrl: string;
  /**
   * Dev-only impersonation OID. When set, the extension passes it as the
   * dev_impersonate_oid query parameter on the SignalR hub URL. Leave empty
   * in production deployments — the extension will then refuse to connect
   * until real auth lands.
   */
  devImpersonateOid: string | null;
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
};

/** chrome.storage.local key the agent-pushed backend URL is persisted under. */
export const BACKEND_URL_KEY = 'backendUrl';

const STORAGE_KEYS: (keyof ExtensionSettings)[] = ['backendUrl', 'devImpersonateOid'];

export async function loadSettings(): Promise<ExtensionSettings> {
  const stored = await chrome.storage.local.get(STORAGE_KEYS);
  const merged: ExtensionSettings = {
    backendUrl: trimTrailingSlash(stringOr(stored.backendUrl, DEFAULTS.backendUrl)),
    devImpersonateOid: nonEmptyOr(stored.devImpersonateOid, DEFAULTS.devImpersonateOid),
  };
  log.debug('settings loaded', { backendUrl: merged.backendUrl, hasImpersonateOid: merged.devImpersonateOid !== null });
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
