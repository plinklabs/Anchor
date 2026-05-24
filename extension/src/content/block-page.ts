import { logger } from '../shared/logger';
import type { ExtensionRuntimeMessage, UnblockRequestPayload } from '../shared/types';

const log = logger('block-page');

interface BlockParams {
  blockedUrl: string;
  sessionId: string | null;
  mode: string | null;
}

function readParams(): BlockParams {
  const url = new URL(globalThis.location.href);
  return {
    blockedUrl: url.searchParams.get('blocked') ?? '',
    sessionId: url.searchParams.get('session'),
    mode: url.searchParams.get('mode'),
  };
}

function render(params: BlockParams): void {
  const urlEl = document.querySelector<HTMLElement>('[data-blocked-url]');
  if (urlEl) urlEl.textContent = params.blockedUrl || '(unknown)';

  const sessionSuffix = document.querySelector<HTMLElement>('[data-session-suffix]');
  if (sessionSuffix && params.mode) {
    // Show the mode (Strict / Loose) but not the session ID — that's noise to
    // a student and useful only in the event log on the backend.
    sessionSuffix.textContent = ` (${params.mode.toLowerCase()})`;
  }
}

function setStatus(text: string, kind: 'info' | 'error' = 'info'): void {
  const el = document.querySelector<HTMLElement>('[data-status]');
  if (!el) return;
  el.textContent = text;
  el.classList.toggle('error', kind === 'error');
}

function extractHost(rawUrl: string): string | null {
  try {
    return new URL(rawUrl).hostname.toLowerCase();
  } catch {
    return null;
  }
}

/**
 * Does an approved host cover the URL we're blocking? Mirrors the matcher's
 * Suffix semantics (#72) — `reddit.com` matches `reddit.com` AND any
 * subdomain. The dashboard always grants Suffix, so this is the only rule
 * we need to honour here.
 */
function hostCoversBlockedUrl(blockedHost: string, approvedHost: string): boolean {
  if (!blockedHost || !approvedHost) return false;
  if (blockedHost === approvedHost) return true;
  return blockedHost.endsWith('.' + approvedHost);
}

function wireButtons(params: BlockParams): void {
  const backBtn = document.querySelector<HTMLButtonElement>('[data-action="back"]');
  if (backBtn) {
    backBtn.addEventListener('click', () => {
      // history.length > 1 is unreliable inside an extension page (the block
      // navigation may be the only entry). Try history.back, fall back to
      // closing the tab if there's nowhere to go.
      if (globalThis.history.length > 1) {
        globalThis.history.back();
      } else {
        globalThis.close();
      }
    });
  }

  const requestBtn = document.querySelector<HTMLButtonElement>('[data-action="request"]');
  if (!requestBtn) return;

  // Without a session we can't attach the request to anything — fall back to
  // a disabled state rather than sending a request the backend would reject.
  if (!params.sessionId) {
    requestBtn.disabled = true;
    setStatus('No active session — reload the page.', 'error');
    return;
  }

  requestBtn.addEventListener('click', async () => {
    requestBtn.disabled = true;
    setStatus('Sending request to your teacher…');

    const host = extractHost(params.blockedUrl) ?? '';
    const payload: UnblockRequestPayload = {
      url: params.blockedUrl,
      host,
    };
    const message: ExtensionRuntimeMessage = {
      kind: 'unblock-request',
      sessionId: params.sessionId!,
      payload,
    };

    try {
      const response = await chrome.runtime.sendMessage(message);
      if (response?.ok) {
        setStatus('Requested — waiting for teacher.');
        log.info('UnblockRequest sent', { host });
      } else {
        // Background ran but the relay failed (hub down, no active session
        // cached, etc.). Surface the actual reason so the student can tell
        // whether to wait or to flag the teacher manually.
        const reason = response?.error ?? 'Unknown error';
        setStatus(`Couldn't reach teacher: ${reason}`, 'error');
        requestBtn.disabled = false;
        log.warn('UnblockRequest rejected by background', { reason });
      }
    } catch (err) {
      // chrome.runtime.sendMessage rejected — usually means the SW was
      // hibernated and didn't wake in time, or has no listener registered.
      // Surface the underlying message so it isn't an opaque "try again".
      const detail = err instanceof Error ? err.message : String(err);
      setStatus(`Couldn't reach teacher: ${detail}`, 'error');
      requestBtn.disabled = false;
      log.error('sendMessage(unblock-request) failed', err);
    }
  });
}

function wireAllowlistAmendedListener(params: BlockParams): void {
  const blockedHost = extractHost(params.blockedUrl);
  if (!blockedHost || !params.sessionId) return;

  chrome.runtime.onMessage.addListener((raw) => {
    const message = raw as ExtensionRuntimeMessage;
    if (message?.kind !== 'allowlist-amended') return;
    if (message.sessionId !== params.sessionId) return;
    if (!message.addedHosts.some((h) => hostCoversBlockedUrl(blockedHost, h))) return;

    log.info('matching allowlist amendment received — redirecting', {
      blockedHost,
      addedHosts: message.addedHosts,
    });
    setStatus('Approved — opening the page…');
    // Replace rather than assign so the block page doesn't sit in history,
    // and the back button doesn't bring it right back.
    globalThis.location.replace(params.blockedUrl);
  });
}

const params = readParams();
log.info('block page loaded', { blockedUrl: params.blockedUrl, sessionId: params.sessionId });
render(params);
wireButtons(params);
wireAllowlistAmendedListener(params);
