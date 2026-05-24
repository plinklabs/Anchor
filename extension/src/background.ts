import { HubClient } from './shared/hub-client';
import { isUrlAllowed } from './shared/host-matcher';
import { logger } from './shared/logger';
import { loadSettings } from './shared/settings';
import {
  clearActiveSession,
  getActiveSession,
  mergeAddedDomains,
  setActiveSession,
} from './shared/session-state';
import type {
  ActiveSessionState,
  AllowlistAmendedPayload,
  ExtensionRuntimeMessage,
  SessionStartedPayload,
  UnblockRequestPayload,
} from './shared/types';

const log = logger('background');

// MV3 service workers are torn down between event bursts. We re-resolve the
// active session from chrome.storage.session on every navigation rather than
// caching it in a top-level variable — top-level state is destroyed when the
// worker hibernates, but storage.session survives until the browser restarts.

const BLOCK_PAGE_FILE = 'block-page.html';

let hubClient: HubClient | null = null;

chrome.runtime.onInstalled.addListener((details) => {
  log.info('extension installed', { reason: details.reason });
});

chrome.runtime.onStartup.addListener(() => {
  log.info('runtime startup');
  void ensureHub();
});

// Service worker waking up after hibernation — re-establish the hub. Re-entry
// is idempotent because ensureHub() guards on the existing instance.
log.info('service worker started');
void ensureHub();

// ---------------------------------------------------------------------------
// Hub lifecycle
// ---------------------------------------------------------------------------

async function ensureHub(): Promise<void> {
  if (hubClient) return;
  const settings = await loadSettings();
  if (!settings.devImpersonateOid) {
    // Production-style auth (chrome.identity / Entra token) is a follow-up
    // issue. Without it, the dev impersonation fallback is the only way to
    // authenticate against the hub. Refuse to connect rather than spin in a
    // 401 loop.
    log.warn('no auth configured — refusing to connect to hub. Set devImpersonateOid in chrome.storage.local for dev.');
    return;
  }
  hubClient = new HubClient(settings, {
    onSessionStarted: handleSessionStarted,
    onSessionEnded: handleSessionEnded,
    onAllowlistAmended: handleAllowlistAmended,
  });
  try {
    await hubClient.start();
  } catch (err) {
    log.error('hub start failed; will rely on automatic reconnect', err);
  }
}

async function handleSessionStarted(payload: SessionStartedPayload): Promise<void> {
  const state: ActiveSessionState = {
    sessionId: payload.sessionId,
    classId: payload.classId,
    mode: payload.mode,
    joinCode: payload.joinCode,
    startedAt: payload.startedAt,
    // Wire shape already matches the matcher's AllowedDomain (camelCase
    // matchType + value), so no field renaming is needed here.
    domains: payload.domains ?? [],
  };
  await setActiveSession(state);
  log.info('active session cached', {
    sessionId: state.sessionId,
    mode: state.mode,
    domainCount: state.domains.length,
  });
}

async function handleSessionEnded(sessionId: string): Promise<void> {
  const current = await getActiveSession();
  if (current && current.sessionId !== sessionId) {
    log.warn('ignoring SessionEnded for a different session', {
      activeSessionId: current.sessionId,
      endedSessionId: sessionId,
    });
    return;
  }
  await clearActiveSession();
  log.info('active session cleared', { sessionId });
}

async function handleAllowlistAmended(payload: AllowlistAmendedPayload): Promise<void> {
  const merged = await mergeAddedDomains(payload.sessionId, payload.addedDomains ?? []);
  if (!merged) {
    log.warn('AllowlistAmended dropped — no matching active session in cache', {
      sessionId: payload.sessionId,
    });
    return;
  }
  log.info('allowlist amended', {
    sessionId: payload.sessionId,
    addedCount: payload.addedDomains?.length ?? 0,
    totalDomains: merged.domains.length,
  });

  // Tell any open block pages that an amendment landed. We send the bare
  // host strings: the block page only needs to know "does this match what
  // I'm currently blocking?" — it doesn't need the matchType to decide.
  const addedHosts = (payload.addedDomains ?? [])
    .map((d) => d.value?.trim().toLowerCase())
    .filter((v): v is string => !!v);
  if (addedHosts.length === 0) return;
  const message: ExtensionRuntimeMessage = {
    kind: 'allowlist-amended',
    sessionId: payload.sessionId,
    addedHosts,
  };
  try {
    // sendMessage with no recipient broadcasts to all extension contexts
    // (popups, options pages, extension pages like the block page). The
    // callback errors silently if no listener is attached — we don't care.
    await chrome.runtime.sendMessage(message);
  } catch (err) {
    // "Receiving end does not exist" is the normal case when no block page
    // is open; not worth surfacing as an error.
    log.debug('no runtime listeners for allowlist-amended (expected if no block page open)', err);
  }
}

// ---------------------------------------------------------------------------
// Block-page → background bridge (UnblockRequest)
// ---------------------------------------------------------------------------

chrome.runtime.onMessage.addListener((raw, _sender, sendResponse) => {
  const message = raw as ExtensionRuntimeMessage;
  if (message?.kind !== 'unblock-request') return undefined;

  void handleUnblockRequestFromPage(message.sessionId, message.payload)
    .then(() => sendResponse({ ok: true }))
    .catch((err) => {
      log.error('unblock-request relay failed', err);
      sendResponse({ ok: false, error: err instanceof Error ? err.message : String(err) });
    });
  // Returning true keeps the message channel open for the async response.
  return true;
});

async function handleUnblockRequestFromPage(
  sessionId: string,
  payload: UnblockRequestPayload,
): Promise<void> {
  if (!hubClient) {
    // Block page can only render when we'd previously cached an active
    // session, so the hub should already be up. If it isn't, surface that
    // — the block page will show a "couldn't reach teacher" message.
    throw new Error('Hub not initialised');
  }
  const current = await getActiveSession();
  if (current?.sessionId !== sessionId) {
    throw new Error('Active session has changed; reload the page.');
  }
  await hubClient.reportUnblockRequest(sessionId, payload);
  log.info('forwarded UnblockRequest', { sessionId, host: payload.host });
}

// ---------------------------------------------------------------------------
// Navigation filtering
// ---------------------------------------------------------------------------

// onBeforeNavigate fires before the browser starts the request, so a redirect
// here lands cleanly without a flash of the blocked page. We only act on
// top-level frames (frameId === 0) — sub-resources and iframes get filtered
// by web requests already loaded inside an allowed page, which is the right
// trade-off (over-blocking iframes breaks logins, search embeds, etc.).
chrome.webNavigation.onBeforeNavigate.addListener(async (details) => {
  if (details.frameId !== 0) return;
  await evaluateAndMaybeBlock(details.tabId, details.url);
});

// SPAs (Outlook, Teams, modern Smartschool) change route via the History API
// without firing onBeforeNavigate. tabs.onUpdated with changeInfo.url catches
// those after-the-fact — we still redirect, but the SPA has already taken a
// (brief) step into off-allowlist territory. Acceptable trade-off for v1.
chrome.tabs.onUpdated.addListener(async (tabId, changeInfo) => {
  if (!changeInfo.url) return;
  await evaluateAndMaybeBlock(tabId, changeInfo.url);
});

async function evaluateAndMaybeBlock(tabId: number, url: string): Promise<void> {
  if (tabId < 0) return; // pre-render, devtools, etc.

  // Don't filter the block page itself, or any extension-internal URL.
  if (url.startsWith(chrome.runtime.getURL(''))) return;

  const session = await getActiveSession();
  if (!session) {
    // No active session → never block (idle state per the design doc).
    return;
  }

  if (session.mode.toLowerCase() === 'loose') {
    // Loose mode = pass everything for now. The known-bad-categories logic
    // lands in a separate issue per the #72 spec.
    return;
  }

  if (isUrlAllowed(url, session.domains)) {
    return;
  }

  log.info('blocking off-allowlist navigation', { tabId, url, sessionId: session.sessionId });
  await redirectToBlockPage(tabId, url, session);
  await reportBlockedUrl(session.sessionId, tabId, url);
}

async function redirectToBlockPage(tabId: number, blockedUrl: string, session: ActiveSessionState): Promise<void> {
  const params = new URLSearchParams({
    blocked: blockedUrl,
    session: session.sessionId,
    mode: session.mode,
  });
  const target = chrome.runtime.getURL(BLOCK_PAGE_FILE) + '?' + params.toString();
  try {
    await chrome.tabs.update(tabId, { url: target });
  } catch (err) {
    log.error('tabs.update to block page failed', err);
  }
}

async function reportBlockedUrl(sessionId: string, tabId: number, blockedUrl: string): Promise<void> {
  if (!hubClient) return;
  let host = '';
  try {
    host = new URL(blockedUrl).hostname;
  } catch {
    // Unparseable — leave host empty; the URL itself is still informative.
  }
  await hubClient.reportBlockedUrl(sessionId, {
    url: blockedUrl,
    host,
    tabId,
    occurredAt: new Date().toISOString(),
  });
}
