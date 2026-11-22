// Why the agent link was down, kept until the link is back (#561).
//
// When the native port drops, Edge puts its reason in chrome.runtime.lastError:
// "Specified native messaging host not found.", "Access to the specified native
// messaging host is forbidden.", "Native host has exited." Read nowhere, it only
// ever reached the service worker's console, which nobody can open on a
// student's laptop. While the link is down the extension can't tell the agent
// (it has no other way out), so it keeps the reason, how often the link failed
// and since when, and sends it the agent as `link_error` once the link is up
// again. The agent logs it, and its log upload (#446) carries it.
//
// Kept in chrome.storage.session: the link can stay down long past the worker's
// idle timeout, and the worker the retry alarm wakes (#572) is a new one. Only
// Edge's message goes in it, never anything the student browses.

import { logger } from './logger';

const log = logger('link-trouble');

/** The storage key the trouble is kept under. */
export const LINK_TROUBLE_KEY = 'anchorLinkTrouble';

/** Edge's message is capped at this length; the agent caps it too. */
export const MAX_ERROR_LENGTH = 300;

/** What went wrong with the link while it was down. */
export interface LinkTrouble {
  /** The reason Edge gave the last time the link failed. */
  error: string;
  /** How often the link failed since `since`. */
  failedAttempts: number;
  /** When it first failed (ISO 8601). */
  since: string;
}

/** The trouble after one more failure with `error`, at `now`. */
export function recordFailure(previous: LinkTrouble | null, error: string, now: Date = new Date()): LinkTrouble {
  const trimmed = error.trim() || 'no reason given';
  return {
    error: trimmed.length > MAX_ERROR_LENGTH ? `${trimmed.slice(0, MAX_ERROR_LENGTH)}…` : trimmed,
    failedAttempts: (previous?.failedAttempts ?? 0) + 1,
    since: previous?.since ?? now.toISOString(),
  };
}

/**
 * `newer` on top of what an earlier worker kept: its count added, its start
 * kept, the newer reason.
 */
export function mergeTrouble(kept: LinkTrouble | null, newer: LinkTrouble | null): LinkTrouble | null {
  if (!kept) return newer;
  if (!newer) return kept;
  return {
    error: newer.error,
    failedAttempts: kept.failedAttempts + newer.failedAttempts,
    since: kept.since < newer.since ? kept.since : newer.since,
  };
}

/** Where the trouble outlives the worker. */
export interface LinkTroubleStore {
  load(): Promise<LinkTrouble | null>;
  save(trouble: LinkTrouble | null): Promise<void>;
}

/** The structural subset of `chrome.storage.session` this uses. */
export interface SessionStorageArea {
  get(key: string): Promise<Record<string, unknown>>;
  set(items: Record<string, unknown>): Promise<void>;
  remove(key: string): Promise<void>;
}

/** The trouble kept in `storage` (chrome.storage.session). A read that fails counts as none kept. */
export function linkTroubleStore(storage: SessionStorageArea): LinkTroubleStore {
  return {
    async load() {
      try {
        const kept = (await storage.get(LINK_TROUBLE_KEY))[LINK_TROUBLE_KEY];
        return isLinkTrouble(kept) ? kept : null;
      } catch (err) {
        log.warn('could not read the kept link trouble', err);
        return null;
      }
    },
    async save(trouble) {
      try {
        if (trouble) await storage.set({ [LINK_TROUBLE_KEY]: trouble });
        else await storage.remove(LINK_TROUBLE_KEY);
      } catch (err) {
        log.warn('could not keep the link trouble', err);
      }
    },
  };
}

function isLinkTrouble(value: unknown): value is LinkTrouble {
  const t = value as LinkTrouble | null;
  return (
    typeof t === 'object' &&
    t !== null &&
    typeof t.error === 'string' &&
    typeof t.failedAttempts === 'number' &&
    typeof t.since === 'string'
  );
}
