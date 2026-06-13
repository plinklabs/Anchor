import { HubClient } from './shared/hub-client';
import { isUrlAllowed } from './shared/host-matcher';
import { logger } from './shared/logger';
import { selectTabsToBlock } from './shared/tab-scan';
import { loadSettings } from './shared/settings';
import { classifyCreatedWindow, isHostAccessLoss } from './shared/tamper';
import {
  clearActiveSession,
  getActiveSession,
  mergeAddedDomains,
  replaceDomains,
  setActiveSession,
} from './shared/session-state';
import type {
  ActiveSessionState,
  AllowlistAmendedPayload,
  ExtensionRuntimeMessage,
  SessionBundlesUpdatedPayload,
  SessionStartedPayload,
  TamperKind,
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
    onSessionBundlesUpdated: handleSessionBundlesUpdated,
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
    joinCode: payload.joinCode,
    startedAt: payload.startedAt,
    // Wire shape already matches the matcher's AllowedDomain (camelCase
    // matchType + value), so no field renaming is needed here.
    domains: payload.domains ?? [],
  };
  await setActiveSession(state);
  log.info('active session cached', {
    sessionId: state.sessionId,
    domainCount: state.domains.length,
  });
  // Catch tabs the student opened before the session started — they predate
  // any navigation event, so only an explicit scan can close that loophole.
  await scanAndBlockOpenTabs(state);
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

async function handleSessionBundlesUpdated(payload: SessionBundlesUpdatedPayload): Promise<void> {
  // Full replacement of this student's domain set after the teacher changed
  // the session's bundles. The payload already folds in the student's unblock
  // grants, so a straight replace can't lose them.
  const next = await replaceDomains(payload.sessionId, payload.domains ?? []);
  if (!next) {
    log.warn('SessionBundlesUpdated dropped — no matching active session in cache', {
      sessionId: payload.sessionId,
    });
    return;
  }
  log.info('active session allowlist replaced', {
    sessionId: payload.sessionId,
    domainCount: next.domains.length,
  });
  // Removing a bundle can turn a currently-open tab off-list, and that tab
  // won't navigate on its own. Re-scan so a mid-session bundle change closes
  // the loophole retroactively, same as it does at session start (#91).
  await scanAndBlockOpenTabs(next);
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

  if (isUrlAllowed(url, session.domains)) {
    return;
  }

  log.info('blocking off-allowlist navigation', { tabId, url, sessionId: session.sessionId });
  await redirectToBlockPage(tabId, url, session);
  await reportBlockedUrl(session.sessionId, tabId, url);
}

// Scan every open tab against the session's allowlist and redirect off-list
// ones to the block page. Triggered by allowlist *arrival* (session start, or
// a mid-session bundle change), not by a timer — the session passed in is the
// allowlist that just landed, so there's no window where this races the
// forward-navigation listeners: both judge against the same cached domains.
async function scanAndBlockOpenTabs(session: ActiveSessionState): Promise<void> {
  let tabs: chrome.tabs.Tab[];
  try {
    tabs = await chrome.tabs.query({});
  } catch (err) {
    log.error('open-tab scan failed: tabs.query rejected', err);
    return;
  }

  const toBlock = selectTabsToBlock(tabs, session.domains, chrome.runtime.getURL(''));
  if (toBlock.length === 0) return;

  log.info('redirecting off-allowlist tabs found at allowlist arrival', {
    sessionId: session.sessionId,
    count: toBlock.length,
  });
  for (const { tabId, url } of toBlock) {
    await redirectToBlockPage(tabId, url, session);
    await reportBlockedUrl(session.sessionId, tabId, url);
  }
}

async function redirectToBlockPage(tabId: number, blockedUrl: string, session: ActiveSessionState): Promise<void> {
  const params = new URLSearchParams({
    blocked: blockedUrl,
    session: session.sessionId,
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

// ---------------------------------------------------------------------------
// Tamper detection (#105)
// ---------------------------------------------------------------------------
// Soft enforcement (design §5.4): make tampering visible to the teacher rather
// than trying to prevent it. These listeners catch what the extension can
// witness itself while running; the agent covers the rest (disabled/removed,
// on-box InPrivate) as on-box witness in a follow-up.

chrome.windows.onCreated.addListener((window) => {
  const kind = classifyCreatedWindow(window);
  if (kind) void reportTamperIfInSession(kind);
});

chrome.permissions.onRemoved.addListener((removed) => {
  if (isHostAccessLoss(removed)) void reportTamperIfInSession('host_permission_revoked');
});

async function reportTamperIfInSession(kind: TamperKind): Promise<void> {
  // Tampering is only actionable while a session is enforcing — outside one the
  // student may browse and reconfigure freely, so an InPrivate window or a
  // permission change isn't a violation (#105, "during session").
  const session = await getActiveSession();
  if (!session) {
    log.debug('tamper signal ignored — no active session', { kind });
    return;
  }
  if (!hubClient) {
    log.warn('tamper signal observed but hub not initialised', { kind });
    return;
  }
  log.warn('tamper detected', { kind, sessionId: session.sessionId });
  await hubClient.reportTamper(session.sessionId, { kind });
}
