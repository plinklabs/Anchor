// Configuration the extension reads on startup. Stored in chrome.storage.local
// so it survives service-worker restarts but stays per-profile (a managed
// rollout will populate this via enterprise policy / managed storage; for
// dev, set the values manually from the background SW devtools console):
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

const DEFAULTS: ExtensionSettings = {
  // Default agent/dashboard/backend port (memory: reference_agent_dashboard_backend_ports).
  backendUrl: 'http://localhost:5276',
  devImpersonateOid: null,
};

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
