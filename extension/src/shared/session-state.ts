// Tiny wrapper over chrome.storage.session so background.ts and the block
// page can both reach the active session without a back-channel message.
// `storage.session` is cleared automatically when the browser restarts,
// which is the behaviour we want — a stale allowlist surviving a reboot
// could silently filter against the wrong session.

import type { ActiveSessionState } from './types';

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

function isActiveSessionState(value: unknown): value is ActiveSessionState {
  if (typeof value !== 'object' || value === null) return false;
  const obj = value as Record<string, unknown>;
  return typeof obj.sessionId === 'string'
    && typeof obj.classId === 'string'
    && typeof obj.mode === 'string'
    && Array.isArray(obj.domains);
}
