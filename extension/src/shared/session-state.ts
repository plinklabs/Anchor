// Tiny wrapper over chrome.storage.session so background.ts and the block
// page can both reach the active session without a back-channel message.
// `storage.session` is cleared automatically when the browser restarts,
// which is the behaviour we want — a stale allowlist surviving a reboot
// could silently filter against the wrong session.

import type { ActiveSessionState, AllowedDomainDto } from './types';

const ACTIVE_SESSION_KEY = 'activeSession';

export async function setActiveSession(state: ActiveSessionState): Promise<void> {
  await chrome.storage.session.set({ [ACTIVE_SESSION_KEY]: state });
}

export async function clearActiveSession(): Promise<void> {
  await chrome.storage.session.remove(ACTIVE_SESSION_KEY);
}

export async function getActiveSession(): Promise<ActiveSessionState | null> {
  const stored = await chrome.storage.session.get(ACTIVE_SESSION_KEY);
  const value = stored[ACTIVE_SESSION_KEY];
  return isActiveSessionState(value) ? value : null;
}

/**
 * Merge teacher-granted domains into the cached active session's allowlist.
 * Idempotent on (matchType, value) so a grant the agent hands down again
 * (#489) doesn't bloat the cache. Returns the new state, or null if there's no
 * active session to amend (in which case the amendment is dropped — a grant
 * for a session we don't know about is stale and would only confuse the
 * filter).
 */
export async function mergeAddedDomains(
  sessionId: string,
  added: ReadonlyArray<AllowedDomainDto>,
): Promise<ActiveSessionState | null> {
  const current = await getActiveSession();
  if (current?.sessionId !== sessionId) return null;
  const next = mergeDomainsInto(current, added);
  if (next === current) return current;
  await setActiveSession(next);
  return next;
}

/**
 * Replace the cached active session's domain set wholesale (#93). Used when the
 * teacher changes the session's bundles mid-session: the agent hands down the
 * full recomputed allowlist (per-student, grants already folded in), so a straight
 * replace is correct and can't lose grants. Returns the new state, or null if
 * there's no matching active session to update (a stale push for an unknown
 * session is dropped rather than applied to the wrong session).
 */
export async function replaceDomains(
  sessionId: string,
  domains: ReadonlyArray<AllowedDomainDto>,
): Promise<ActiveSessionState | null> {
  const current = await getActiveSession();
  if (current?.sessionId !== sessionId) return null;
  const next: ActiveSessionState = { ...current, domains: [...domains] };
  await setActiveSession(next);
  return next;
}

/**
 * Pure dedup-merge so the logic can be unit-tested without chrome.storage.
 * Returns the same reference when no changes are needed; that lets callers
 * skip the storage round-trip on a no-op.
 */
export function mergeDomainsInto(
  state: ActiveSessionState,
  added: ReadonlyArray<AllowedDomainDto>,
): ActiveSessionState {
  if (added.length === 0) return state;

  const seen = new Set<string>();
  for (const existing of state.domains) {
    seen.add(domainKey(existing.matchType, existing.value));
  }
  const merged = [...state.domains];
  let changed = false;
  for (const dto of added) {
    if (!dto.value) continue;
    const key = domainKey(dto.matchType, dto.value);
    if (seen.has(key)) continue;
    seen.add(key);
    merged.push(dto);
    changed = true;
  }
  return changed ? { ...state, domains: merged } : state;
}

function domainKey(matchType: string, value: string): string {
  return `${matchType} ${value.trim().toLowerCase()}`;
}

function isActiveSessionState(value: unknown): value is ActiveSessionState {
  if (typeof value !== 'object' || value === null) return false;
  const obj = value as Record<string, unknown>;
  return typeof obj.sessionId === 'string'
    && typeof obj.classId === 'string'
    && Array.isArray(obj.domains);
}
