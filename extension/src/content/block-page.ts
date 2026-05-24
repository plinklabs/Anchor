import { logger } from '../shared/logger';

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

function wireButtons(): void {
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
  if (requestBtn) {
    // Wiring this up is the next issue. Leaving it visible-but-disabled so the
    // affordance is real and the next PR only has to flip a flag and add the
    // sendMessage to the background.
    requestBtn.addEventListener('click', () => {
      log.info('Request access clicked — wiring lands in a follow-up issue');
    });
  }
}

const params = readParams();
log.info('block page loaded', { blockedUrl: params.blockedUrl, sessionId: params.sessionId });
render(params);
wireButtons();
