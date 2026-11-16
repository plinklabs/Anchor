import { isUrlAllowed } from './shared/host-matcher';
import { logger } from './shared/logger';
import { selectTabsToBlock } from './shared/tab-scan';
import { selectTabsToRestore } from './shared/tab-restore';
import { classifyCreatedWindow, isHostAccessLoss } from './shared/tamper';
import { WitnessClient, WITNESS_HOST_NAME, type AgentMessage } from './shared/witness';
import {
  clearActiveSession,
  getActiveSession,
  mergeAddedDomains,
  replaceDomains,
  setActiveSession,
} from './shared/session-state';
import type {
  ActiveSessionState,
  AllowedDomainDto,
  ExtensionRuntimeMessage,
  SessionStartedPayload,
  TamperKind,
  UnblockRequestPayload,
} from './shared/types';

const log = logger('background');

// MV3 service workers are torn down between event bursts. We re-resolve the
// active session from chrome.storage.session on every navigation rather than
// caching it in a top-level variable — top-level state is destroyed when the
// worker hibernates, but storage.session survives until the browser restarts.
//
// The extension has no identity of its own (#489). It doesn't sign in and has
// no backend connection: the on-box agent, signed in with the student's Windows
// account, hands it the session it joined over the native-messaging witness link
// and reports what the extension sends back with its own connection. So the
// extension filters for exactly the session the student is in, as that student,
// whatever account the browser is signed in with. Without the agent there is no
// session to enforce, and during one the agent pauses Edge while the link is
// down (#427).

const BLOCK_PAGE_FILE = 'block-page.html';

let witness: WitnessClient | null = null;

chrome.runtime.onInstalled.addListener((details) => {
  log.info('extension installed', { reason: details.reason });
});

chrome.runtime.onStartup.addListener(() => {
  log.info('runtime startup');
  ensureWitness();
});

// Service worker waking up after hibernation — re-open the agent link. Re-entry
// is idempotent because ensureWitness() guards on the existing instance.
log.info('service worker started');
ensureWitness();

// ---------------------------------------------------------------------------
// The agent link (#146 part 1, #489)
// ---------------------------------------------------------------------------

// Always on: the open native port keeps the service worker alive while the
// agent is there, and the agent hands down the current session (or that there
// is none) on every connect — so a worker revived mid-session, an Edge restart,
// or the extension switched back on picks up exactly where the student is.
function ensureWitness(): void {
  if (witness) return;
  witness = new WitnessClient({
    connect: () => chrome.runtime.connectNative(WITNESS_HOST_NAME),
    onAgentMessage: (message) => enqueueAgentMessage(message),
    // The agent going away doesn't end the session: the extension keeps
    // enforcing the one it has until the agent is back and says otherwise. The
    // teacher sees the agent go through its own missing heartbeat.
    onAgentUnavailable: () => log.warn('the Anchor app went away; keeping the session in force'),
  });
  witness.start();
}

// Each handler reads and writes chrome.storage.session; run them one at a time,
// in arrival order, so a session_ended can't overtake the session_started before
// it.
let agentMessages: Promise<void> = Promise.resolve();

function enqueueAgentMessage(message: AgentMessage): void {
  agentMessages = agentMessages
    .then(() => handleAgentMessage(message))
    .catch((err) => log.error('handling an agent message failed', { type: message.type, err }));
}

async function handleAgentMessage(message: AgentMessage): Promise<void> {
  switch (message.type) {
    case 'session_started':
      await handleSessionStarted(message);
      return;
    case 'no_session':
      await handleNoSession();
      return;
    case 'session_ended':
      await handleSessionEnded(message.sessionId);
      return;
    case 'allowlist_replaced':
      await handleAllowlistReplaced(message.sessionId, message.domains);
      return;
    case 'allowlist_amended':
      await handleAllowlistAmended(message.sessionId, message.addedDomains);
      return;
  }
}

// The student is in this session: sent when the agent joined it, and again on
// every (re)connect while they are in it, with the allowlist as it stands then.
async function handleSessionStarted(payload: SessionStartedPayload): Promise<void> {
  const current = await getActiveSession();
  if (current && current.sessionId !== payload.sessionId) {
    // A session we missed the end of while the link was down.
    log.info('a newer session replaces the cached one', {
      cachedSessionId: current.sessionId,
      sessionId: payload.sessionId,
    });
    await endSession(current.sessionId);
  }

  const state: ActiveSessionState = {
    sessionId: payload.sessionId,
    classId: payload.classId,
    joinCode: payload.joinCode,
    startedAt: payload.startedAt,
    // Wire shape already matches the matcher's AllowedDomain (camelCase
    // matchType + value), so no field renaming is needed here.
    domains: [...payload.domains],
  };
  await setActiveSession(state);
  log.info('active session cached', {
    sessionId: state.sessionId,
    domainCount: state.domains.length,
  });
  // A reconnect can bring a wider allowlist than the cached one (a grant or a
  // bundle the extension missed): let block pages it now covers go back.
  await notifyBlockPages(state);
  // Catch tabs the student opened before the session started — they predate
  // any navigation event, so only an explicit scan can close that loophole.
  await scanAndBlockOpenTabs(state);
}

// The agent is in no session: the one cached here ended, or the student left
// it, while the link was down.
async function handleNoSession(): Promise<void> {
  const current = await getActiveSession();
  if (!current) return;
  log.info('the agent is in no session; ending the cached one', { sessionId: current.sessionId });
  await endSession(current.sessionId);
}

// The session ended, or the student left it (#462).
async function handleSessionEnded(sessionId: string): Promise<void> {
  const current = await getActiveSession();
  if (current && current.sessionId !== sessionId) {
    log.warn('ignoring the end of a session other than the active one', {
      activeSessionId: current.sessionId,
      endedSessionId: sessionId,
    });
    return;
  }
  await endSession(sessionId);
}

// Drop the session and send the tabs parked on its block page back to their
// pages.
async function endSession(sessionId: string): Promise<void> {
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

async function handleAllowlistAmended(
  sessionId: string,
  addedDomains: ReadonlyArray<AllowedDomainDto>,
): Promise<void> {
  const merged = await mergeAddedDomains(sessionId, addedDomains);
  if (!merged) {
    log.warn('allowlist amendment dropped — no matching active session in cache', { sessionId });
    return;
  }
  log.info('allowlist amended', {
    sessionId,
    addedCount: addedDomains.length,
    totalDomains: merged.domains.length,
  });
  await notifyBlockPages(merged);
}

// Tell any open block pages which allowlist is now in force, so one whose
// blocked URL it covers sends the student back there. We send the full list
// with its match types: the block page then judges with the same matcher as
// the navigation filter, wildcard entries included (#426), instead of guessing
// from bare host strings.
async function notifyBlockPages(state: ActiveSessionState): Promise<void> {
  const message: ExtensionRuntimeMessage = {
    kind: 'allowlist-amended',
    sessionId: state.sessionId,
    domains: state.domains,
  };
  try {
    // sendMessage with no recipient broadcasts to all extension contexts
    // (popups, options pages, extension pages like the block page).
    await chrome.runtime.sendMessage(message);
  } catch (err) {
    // "Receiving end does not exist" is the normal case when no block page
    // is open; not worth surfacing as an error.
    log.debug('no runtime listeners for allowlist-amended (expected if no block page open)', err);
  }
}

async function handleAllowlistReplaced(
  sessionId: string,
  domains: ReadonlyArray<AllowedDomainDto>,
): Promise<void> {
  // Full replacement of this student's domain set after the teacher changed
  // the session's bundles. The list already folds in the student's unblock
  // grants, so a straight replace can't lose them.
  const next = await replaceDomains(sessionId, domains);
  if (!next) {
    log.warn('allowlist replacement dropped — no matching active session in cache', { sessionId });
    return;
  }
  log.info('active session allowlist replaced', {
    sessionId,
    domainCount: next.domains.length,
  });
  // Adding a bundle can cover a tab already parked on the block page: the
  // session starts with no bundles, so the scan at its start blocks hosts the
  // teacher allows moments later (#426). Those block pages don't navigate on
  // their own, so tell them, the same way an approved unblock request does.
  // First, so the student isn't kept waiting on the scan's per-tab reports.
  await notifyBlockPages(next);
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
  const current = await getActiveSession();
  if (current?.sessionId !== sessionId) {
    throw new Error('Active session has changed; reload the page.');
  }
  if (!witness) {
    throw new Error('The Anchor app is not connected.');
  }
  // The agent sends it to the teacher and answers; a refusal or no answer
  // surfaces on the block page, so the request never silently vanishes.
  const result = await witness.requestUnblock({ sessionId, ...payload });
  if (!result.ok) {
    throw new Error(result.error ?? 'The Anchor app could not send the request.');
  }
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
  reportBlockedUrl(session.sessionId, tabId, url);
}

// Scan every open tab against the session's allowlist and redirect off-list
// ones to the block page. Triggered by allowlist *arrival* (session start, a
// reconnect, or a mid-session bundle change), not by a timer — the session
// passed in is the allowlist that just landed, so there's no window where this
// races the forward-navigation listeners: both judge against the same cached
// domains.
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
    reportBlockedUrl(session.sessionId, tabId, url);
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

// The agent sends it on to the teacher's session log as a BlockedUrl event.
function reportBlockedUrl(sessionId: string, tabId: number, blockedUrl: string): void {
  let host = '';
  try {
    host = new URL(blockedUrl).hostname;
  } catch {
    // Unparseable — leave host empty; the URL itself is still informative.
  }
  witness?.report({
    type: 'blocked_url',
    sessionId,
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
// witness itself while running, and hand it to the agent to report; the agent
// covers what the extension cannot witness about itself (disabled/removed).

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
  log.warn('tamper detected', { kind, sessionId: session.sessionId });
  witness?.report({ type: 'tamper', sessionId: session.sessionId, kind });
}
