// The interactive sign-in gate (#331).
//
// #289 bounded the student's interactive sign-in to "one window per service-worker
// lifetime" with a module-level flag in background.ts. MV3 tears the worker down
// between event bursts, so that flag reset on every revival — and a *failing* hub
// connect is itself what wakes the worker again. With a permanently broken auth
// config (the live symptom in #331 was AADSTS700051: implicit access-token
// issuance not enabled on the app registration) every revival was therefore
// allowed one more sign-in window: closing the window brought it straight back,
// which made the machine unusable.
//
// The guard state consequently cannot live in worker memory. It lives in
// chrome.storage.session, the same store the active session uses
// (session-state.ts): it survives worker hibernation but is cleared when the
// browser restarts, so a fresh browser session still gets one honest prompt.
// On top of that, each attempt schedules an exponential backoff, so even inside
// one browser session a broken config opens a handful of windows spaced minutes
// apart instead of one per revival.
//
// The same record carries the last hard failure so the surfaces that report
// extension state (the toolbar badge and the popup) can name the cause instead
// of leaving the student with a silently dead hub connection.

import { logger } from './logger';

const log = logger('auth-gate');

/** chrome.storage.session key the gate is persisted under. */
export const AUTH_GATE_KEY = 'authGate';

/** A hard authentication failure, kept so the badge/popup can name the cause. */
export interface AuthFailure {
  /** The Entra error code (`AADSTS…`) when the failure carried one, else null. */
  code: string | null;
  /** The failure text, as reported by the flow that failed. */
  message: string;
  /** Epoch-ms at which the failure was recorded. */
  at: number;
}

export interface AuthGateState {
  /** Interactive sign-in windows opened since the last successful token. */
  attempts: number;
  /** Epoch-ms before which no further interactive sign-in may be opened. */
  nextAttemptAt: number;
  /** The last hard failure, or undefined while auth is healthy. */
  failure?: AuthFailure;
}

/** A gate that has never fired: interactive sign-in is allowed immediately. */
const EMPTY_GATE: AuthGateState = { attempts: 0, nextAttemptAt: 0 };

/**
 * Backoff after the n-th consecutive interactive attempt, in ms. Deliberately
 * minutes, not seconds: the failure mode this defends against is a *config*
 * error that no amount of retrying fixes, and the student is staring at the
 * window. The silent (`prompt=none`) flow is never gated, so a repaired config
 * still recovers on the very next connect without waiting out the backoff.
 */
const BACKOFF_LADDER_MS = [2 * 60_000, 10 * 60_000, 30 * 60_000, 60 * 60_000];

export function backoffMs(attempts: number): number {
  if (attempts < 1) return 0;
  const index = Math.min(attempts, BACKOFF_LADDER_MS.length) - 1;
  return BACKOFF_LADDER_MS[index];
}

/**
 * Decide whether an interactive sign-in may open now, and what the gate should
 * look like afterwards. Pure so the ladder can be unit-tested without storage.
 */
export function planInteractiveClaim(
  state: AuthGateState,
  now: number,
): { allowed: boolean; next: AuthGateState } {
  if (now < state.nextAttemptAt) return { allowed: false, next: state };
  const attempts = state.attempts + 1;
  return {
    allowed: true,
    next: { ...state, attempts, nextAttemptAt: now + backoffMs(attempts) },
  };
}

/**
 * Turn whatever the auth flow threw into a record the UI can show. Entra folds
 * its diagnostic code into the error description (`AADSTS700051: response_type
 * 'token' is not enabled for the application.`), so pulling that code out gives
 * the one string a teacher or IT desk can actually act on.
 */
export function describeAuthFailure(err: unknown, now: number = Date.now()): AuthFailure {
  const message = err instanceof Error ? err.message : String(err);
  const code = /AADSTS\d+/.exec(message)?.[0] ?? null;
  return { code, message: message.trim() || 'Unknown authentication error', at: now };
}

/** Read the persisted gate, tolerating an absent or malformed record. */
export async function readAuthGate(): Promise<AuthGateState> {
  try {
    const stored = await chrome.storage.session.get(AUTH_GATE_KEY);
    return parseAuthGate(stored[AUTH_GATE_KEY]);
  } catch (err) {
    // storage.session can reject in an unusual context; a missing gate is safe
    // (it only ever *permits* a prompt the caller was about to make anyway).
    log.debug('could not read the auth gate', err);
    return EMPTY_GATE;
  }
}

/**
 * Take the next interactive sign-in slot if one is due. Returns false while the
 * backoff from an earlier attempt is still running — the caller must then stay
 * silent rather than open a window.
 */
export async function claimInteractiveSignIn(now: number = Date.now()): Promise<boolean> {
  const state = await readAuthGate();
  const { allowed, next } = planInteractiveClaim(state, now);
  if (!allowed) {
    log.warn('interactive sign-in suppressed — backing off after an earlier failure', {
      attempts: state.attempts,
      retryInMs: state.nextAttemptAt - now,
      lastError: state.failure?.code ?? state.failure?.message,
    });
    return false;
  }
  await writeAuthGate(next);
  return true;
}

/**
 * Record a hard authentication failure and return it. The attempt counter is
 * left as the claim set it — this only attaches the cause, so the badge/popup
 * can explain a dead hub connection instead of failing silently.
 */
export async function recordAuthFailure(
  err: unknown,
  now: number = Date.now(),
): Promise<AuthFailure> {
  const failure = describeAuthFailure(err, now);
  const state = await readAuthGate();
  await writeAuthGate({ ...state, failure });
  return failure;
}

/**
 * Clear the gate after a successful token (or when the agent hands down a new
 * auth config): the next connect is free to prompt again, and the recorded
 * failure disappears from the badge/popup.
 */
export async function clearAuthGate(): Promise<void> {
  try {
    await chrome.storage.session.remove(AUTH_GATE_KEY);
  } catch (err) {
    log.debug('could not clear the auth gate', err);
  }
}

/** The last hard auth failure, for the surfaces that report extension state. */
export async function getAuthFailure(): Promise<AuthFailure | null> {
  return (await readAuthGate()).failure ?? null;
}

async function writeAuthGate(state: AuthGateState): Promise<void> {
  try {
    await chrome.storage.session.set({ [AUTH_GATE_KEY]: state });
  } catch (err) {
    // Losing the write degrades to the pre-#331 behaviour rather than breaking
    // sign-in, so it is worth a line but not a thrown error.
    log.warn('could not persist the auth gate — the sign-in backoff may not survive a worker restart', err);
  }
}

function parseAuthGate(value: unknown): AuthGateState {
  if (typeof value !== 'object' || value === null) return EMPTY_GATE;
  const v = value as Record<string, unknown>;
  const attempts = typeof v.attempts === 'number' && v.attempts >= 0 ? v.attempts : 0;
  const nextAttemptAt = typeof v.nextAttemptAt === 'number' ? v.nextAttemptAt : 0;
  const failure = parseFailure(v.failure);
  return failure ? { attempts, nextAttemptAt, failure } : { attempts, nextAttemptAt };
}

function parseFailure(value: unknown): AuthFailure | undefined {
  if (typeof value !== 'object' || value === null) return undefined;
  const v = value as Record<string, unknown>;
  if (typeof v.message !== 'string' || v.message.length === 0) return undefined;
  return {
    code: typeof v.code === 'string' ? v.code : null,
    message: v.message,
    at: typeof v.at === 'number' ? v.at : 0,
  };
}
