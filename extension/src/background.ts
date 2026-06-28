import { HubClient } from './shared/hub-client';
import { SessionHeartbeat } from './shared/heartbeat';
import { isUrlAllowed } from './shared/host-matcher';
import { logger } from './shared/logger';
import { selectTabsToBlock } from './shared/tab-scan';
import { selectTabsToRestore } from './shared/tab-restore';
import { loadSettings, persistBackendUrl, persistAuthConfig, resolveAuthMode } from './shared/settings';
import { EntraAuthenticator } from './shared/auth';
import type { AuthConfig } from './shared/settings';
import { classifyCreatedWindow, isHostAccessLoss } from './shared/tamper';
import { WitnessClient, WITNESS_HOST_NAME } from './shared/witness';
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
let witness: WitnessClient | null = null;
let heartbeat: SessionHeartbeat | null = null;
let authenticator: EntraAuthenticator | null = null;
// Bounds interactive sign-in to at most one window per service-worker lifetime
// (#289): a dismissed prompt must not re-pop on every SignalR reconnect. Reset
// when a new auth config arrives or the worker restarts (module state is lost on
// hibernation), so a genuinely-signed-out student is re-prompted next wake.
let interactiveSignInTried = false;

chrome.runtime.onInstalled.addListener((details) => {
  log.info('extension installed', { reason: details.reason });
});

chrome.runtime.onStartup.addListener(() => {
  log.info('runtime startup');
  void ensureHub();
  ensureWitness();
});

// Service worker waking up after hibernation — re-establish the hub. Re-entry
// is idempotent because ensureHub() guards on the existing instance.
log.info('service worker started');
void ensureHub();
ensureWitness();

// ---------------------------------------------------------------------------
// Hub lifecycle
// ---------------------------------------------------------------------------

async function ensureHub(): Promise<void> {
  if (hubClient) return;
  const settings = await loadSettings();
  const mode = resolveAuthMode(settings);

  const callbacks = {
    onSessionStarted: handleSessionStarted,
    onSessionEnded: handleSessionEnded,
    onAllowlistAmended: handleAllowlistAmended,
    onSessionBundlesUpdated: handleSessionBundlesUpdated,
  };

  if (mode === 'none') {
    // Neither the dev impersonation shortcut nor a production auth config is set,
    // so the extension can't authenticate yet — the agent hasn't handed down its
    // auth_config (or this is a misconfigured dev box). Refuse rather than spin in
    // a 401 loop; ensureHub re-runs when the agent pushes config (#289 / #204).
    log.warn('no auth configured — refusing to connect to hub (awaiting agent auth_config, or set devImpersonateOid for dev)');
    return;
  }

  if (mode === 'token') {
    // Production: acquire a real Entra access token (#289). The factory rides
    // Edge's existing Office 365 session silently; it permits one interactive
    // sign-in per worker lifetime if SSO can't satisfy it, then stays silent.
    authenticator ??= new EntraAuthenticator(settings.authConfig!);
    hubClient = new HubClient(settings, callbacks, () => {
      const allowInteractive = !interactiveSignInTried;
      interactiveSignInTried = true;
      return authenticator!.getToken({ allowInteractive });
    });
  } else {
    // Dev: the impersonation OID rides the hub URL query string (no token).
    hubClient = new HubClient(settings, callbacks);
  }

  try {
    await hubClient.start();
  } catch (err) {
    log.error('hub start failed; will rely on automatic reconnect', err);
  }
  ensureHeartbeat();
}

// Extension witness heartbeat (#149). Always-on like the hub and the native
// witness: the loop only pings while a session is active, and a connected hub
// keeps the service worker alive so it ticks reliably. Started here (not on
// SessionStarted) so a worker revived mid-session by a navigation event resumes
// pinging without waiting for the next SessionStarted.
function ensureHeartbeat(): void {
  if (heartbeat) return;
  heartbeat = new SessionHeartbeat({
    sendHeartbeat: (sessionId) => hubClient?.sendExtensionHeartbeat(sessionId),
    getActiveSessionId: async () => (await getActiveSession())?.sessionId ?? null,
  });
  heartbeat.start();
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
  await restoreRedirectedTabs(sessionId);
}

// On session end, navigate any tab Anchor parked on the block page for this
// session back to the page it was showing before — the original URL is encoded
// in the block-page URL (#121), so no separate state store is needed.
async function restoreRedirectedTabs(sessionId: string): Promise<void> {
  let tabs: chrome.tabs.Tab[];
  try {
    tabs = await chrome.tabs.query({});
  } catch (err) {
    log.error('tab restore failed: tabs.query rejected', err);
    return;
  }

  const toRestore = selectTabsToRestore(
    tabs,
    chrome.runtime.getURL(''),
    BLOCK_PAGE_FILE,
    sessionId,
  );
  if (toRestore.length === 0) return;

  log.info('restoring tabs redirected during the ended session', {
    sessionId,
    count: toRestore.length,
  });
  for (const { tabId, url } of toRestore) {
    try {
      await chrome.tabs.update(tabId, { url });
    } catch (err) {
      log.error('tabs.update to restore original url failed', { tabId, err });
    }
  }
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
// Tamper detection (#105, #146)
// ---------------------------------------------------------------------------
// Soft enforcement (design §5.4): make tampering visible to the teacher rather
// than trying to prevent it. These listeners catch what the extension can
// witness itself while running; the agent covers what the extension cannot
// witness about itself (disabled/removed) as the on-box witness, over the
// native-messaging link below (#146 part 1).

chrome.windows.onCreated.addListener((window) => {
  const kind = classifyCreatedWindow(window);
  if (kind) void reportTamperIfInSession(kind);
});

chrome.permissions.onRemoved.addListener((removed) => {
  if (isHostAccessLoss(removed)) void reportTamperIfInSession('host_permission_revoked');
});

// The native-messaging witness link to the on-box FocusAgent (#146 part 1). The
// agent watches this link to detect the extension being disabled/removed (the
// browser tears the host down → the agent's pipe drops); in return the host
// tells us when the *agent* went away, which we surface as `agent_unavailable`.
// Always-on like the hub: the SignalR connection already keeps the service
// worker alive, and reporting stays gated to an active session.
function ensureWitness(): void {
  if (witness) return;
  witness = new WitnessClient({
    connect: () => chrome.runtime.connectNative(WITNESS_HOST_NAME),
    onAgentUnavailable: () => void reportTamperIfInSession('agent_unavailable'),
    onBackendUrl: (url) => void handleBackendUrlFromAgent(url),
    onAuthConfig: (config) => void handleAuthConfigFromAgent(config),
  });
  witness.start();
}

// The agent is the source of truth for which backend a deployment targets
// (#204). It hands the URL down over the witness link; we persist it and, if it
// changed (or the hub never came up because nothing told us a backend yet),
// (re)establish the hub against it. Only the registered native host can reach
// this path, so an arbitrary web page can't repoint the extension.
async function handleBackendUrlFromAgent(url: string): Promise<void> {
  let changed: boolean;
  try {
    changed = await persistBackendUrl(url);
  } catch (err) {
    log.error('failed to persist agent-provided backend url', err);
    return;
  }
  if (changed && hubClient) {
    // Tear the existing hub down so the next ensureHub() rebuilds it against
    // the new backend; loadSettings() re-reads the URL we just stored.
    log.info('backend url changed — restarting hub onto the new backend');
    try {
      await hubClient.stop();
    } catch (err) {
      log.debug('stopping hub before backend switch threw', err);
    }
    hubClient = null;
  }
  await ensureHub();
}

// The agent is also the source of truth for the deployment's Entra auth config
// (#289), handed down over the same witness link so one published extension serves
// every fork. We persist it and, if it changed (or the hub never came up because
// no auth was configured yet), rebuild the hub — and the authenticator — against
// it. Same trust boundary as the backend URL: only the registered native host can
// reach this path.
async function handleAuthConfigFromAgent(config: AuthConfig): Promise<void> {
  let changed: boolean;
  try {
    changed = await persistAuthConfig(config);
  } catch (err) {
    log.error('failed to persist agent-provided auth config', err);
    return;
  }
  if (changed) {
    // Drop the cached authenticator + the per-worker interactive guard so the
    // next connect re-acquires a token under the new tenant/client/scope.
    authenticator = null;
    interactiveSignInTried = false;
    if (hubClient) {
      log.info('auth config changed — restarting hub under the new auth config');
      try {
        await hubClient.stop();
      } catch (err) {
        log.debug('stopping hub before auth switch threw', err);
      }
      hubClient = null;
    }
  }
  await ensureHub();
}

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
