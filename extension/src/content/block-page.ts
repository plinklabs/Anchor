import { logger } from '../shared/logger';
import { localizeDocument, t } from '../shared/i18n';
import { isBlockLifted, type BlockedPage } from '../shared/block-release';
import type { ExtensionRuntimeMessage, UnblockRequestPayload } from '../shared/types';

const log = logger('block-page');

type BlockParams = BlockedPage;

function readParams(): BlockParams {
  const url = new URL(globalThis.location.href);
  return {
    blockedUrl: url.searchParams.get('blocked') ?? '',
    sessionId: url.searchParams.get('session'),
  };
}

function render(params: BlockParams): void {
  const urlEl = document.querySelector<HTMLElement>('[data-blocked-url]');
  if (urlEl) urlEl.textContent = params.blockedUrl || t('blockUnknownUrl');
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
    setStatus(t('blockNoSession'), 'error');
    return;
  }

  requestBtn.addEventListener('click', async () => {
    requestBtn.disabled = true;
    setStatus(t('blockSendingRequest'));

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
        setStatus(t('blockRequested'));
        log.info('UnblockRequest sent', { host });
      } else {
        // Background ran but the relay failed (hub down, no active session
        // cached, etc.). Surface the actual reason so the student can tell
        // whether to wait or to flag the teacher manually.
        const reason = response?.error ?? t('blockReasonUnknown');
        setStatus(t('blockReachTeacherFailed', reason), 'error');
        requestBtn.disabled = false;
        log.warn('UnblockRequest rejected by background', { reason });
      }
    } catch (err) {
      // chrome.runtime.sendMessage rejected — usually means the SW was
      // hibernated and didn't wake in time, or has no listener registered.
      // Surface the underlying message so it isn't an opaque "try again".
      const detail = err instanceof Error ? err.message : String(err);
      setStatus(t('blockReachTeacherFailed', detail), 'error');
      requestBtn.disabled = false;
      log.error('sendMessage(unblock-request) failed', err);
    }
  });
}

// The background broadcasts the allowlist whenever it changes mid-session: an
// approved unblock request (#73) or a bundle change (#426). When it now covers
// the page we're holding back, go back there.
function wireAllowlistAmendedListener(params: BlockParams): void {
  if (!params.sessionId) return;

  chrome.runtime.onMessage.addListener((raw) => {
    const message = raw as ExtensionRuntimeMessage;
    if (message?.kind !== 'allowlist-amended') return;
    if (!isBlockLifted(params, message.sessionId, message.domains)) return;

    log.info('allowlist now covers the blocked page — redirecting', {
      blockedHost: extractHost(params.blockedUrl),
      domainCount: message.domains.length,
    });
    setStatus(t('blockApproved'));
    // Replace rather than assign so the block page doesn't sit in history,
    // and the back button doesn't bring it right back.
    globalThis.location.replace(params.blockedUrl);
  });
}

const params = readParams();
log.info('block page loaded', { blockedUrl: params.blockedUrl, sessionId: params.sessionId });
// Localize the static copy first, then paint the dynamic bits over it so the
// translated page never flashes its English source.
localizeDocument();
render(params);
wireButtons(params);
wireAllowlistAmendedListener(params);
