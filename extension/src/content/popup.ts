import { logger } from '../shared/logger';
import { localizeDocument, t } from '../shared/i18n';
import { getActiveSession } from '../shared/session-state';
import type { ActiveSessionState } from '../shared/types';

const log = logger('popup');

/**
 * The popup is a *view* over the session the background worker already cached in
 * chrome.storage.session — it never reaches the hub itself. The student opens it
 * to answer two questions: "am I in a session right now?" and "which sites am I
 * allowed to use?". So this file is pure read-and-render: pull the cached
 * ActiveSessionState (or null) and paint the active / idle face of popup.html.
 */

/** Human-friendly hostnames to list as "allowed sites" for the active session.
 *  Deduped and sorted so the list is stable between opens; an empty result is a
 *  legitimate state (baseline-only allowlist) the view renders distinctly. */
export function allowedSiteLabels(session: ActiveSessionState): string[] {
  const seen = new Set<string>();
  for (const domain of session.domains) {
    const value = domain.value?.trim().toLowerCase();
    if (value) seen.add(value);
  }
  return [...seen].sort((a, b) => a.localeCompare(b));
}

function setText(selector: string, text: string): void {
  const el = document.querySelector<HTMLElement>(selector);
  if (el) el.textContent = text;
}

function renderActive(session: ActiveSessionState): void {
  const root = document.querySelector<HTMLElement>('[data-popup-root]');
  root?.classList.add('is-active');

  // The join code is only worth showing when we actually have one cached.
  const joinWrap = document.querySelector<HTMLElement>('[data-joincode]');
  if (joinWrap) {
    if (session.joinCode) {
      joinWrap.hidden = false;
      setText('[data-joincode-value]', session.joinCode);
    } else {
      joinWrap.hidden = true;
    }
  }

  const sites = allowedSiteLabels(session);
  const list = document.querySelector<HTMLUListElement>('[data-allowlist]');
  const empty = document.querySelector<HTMLElement>('[data-allowlist-empty]');
  if (sites.length === 0) {
    if (list) list.hidden = true;
    if (empty) empty.hidden = false;
    return;
  }
  if (empty) empty.hidden = true;
  if (list) {
    list.hidden = false;
    list.replaceChildren(
      ...sites.map((site) => {
        const li = document.createElement('li');
        li.textContent = site;
        return li;
      }),
    );
  }
}

function renderIdle(): void {
  const root = document.querySelector<HTMLElement>('[data-popup-root]');
  root?.classList.remove('is-active');
  // Freeze the living ping in the idle state so the popup doesn't imply an
  // active session — the DS `--static` variant dims and stops the rings.
  const ping = document.querySelector<HTMLElement>('[data-ping]');
  ping?.classList.remove('pl-ping--pulse');
  ping?.classList.add('pl-ping--static');
  setText('[data-eyebrow-label]', t('popupEyebrowIdle'));
}

async function main(): Promise<void> {
  // Translate the static copy up front; renderActive/renderIdle then paint the
  // session-specific bits (and the idle eyebrow) over the localized page.
  localizeDocument();

  let session: ActiveSessionState | null = null;
  try {
    session = await getActiveSession();
  } catch (err) {
    // storage.session can reject if the popup outlives its context; fall back
    // to the idle face rather than throwing into an empty popup.
    log.error('failed to read active session', err);
  }

  if (session) {
    log.info('popup opened with active session', {
      sessionId: session.sessionId,
      domainCount: session.domains.length,
    });
    renderActive(session);
  } else {
    log.info('popup opened with no active session');
    renderIdle();
  }
}

// Auto-run only in a real document (the popup page). Guarding this keeps the
// module importable from unit tests — which exercise allowedSiteLabels in a
// DOM-less Node env — without firing the render against a missing `document`.
if (typeof document !== 'undefined') {
  void main();
}
