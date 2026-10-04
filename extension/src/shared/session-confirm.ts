// Catching up on a session that ended while the extension was offline (#354),
// and on one that started without the extension hearing it (#356).
//
// The backend tells clients that a session ended with a SessionEnded broadcast,
// and a broadcast reaches only the hub connections open when it is sent. The
// extension's connection is replaced all the time — every service-worker
// revival opens a new one, and a network drop or a laptop going to sleep forces
// a reconnect — so a session can end while the extension isn't there to hear
// it. It would then keep blocking against the ended session's allowlist until
// the browser restarted. So each time the hub connects, the extension asks the
// backend whether the student is still in the session it is enforcing, and
// ends it if not.
//
// SessionStarted goes out the same way, so a session can also start without the
// extension hearing it: while it is offline, or in the moment straight after it
// connects — the connection is up before the backend has added it to the
// student's user group, which the broadcast goes to. So it also asks which
// session the student is in, and starts it if it isn't enforcing that one yet.
// Same injected-deps shape as SessionHeartbeat so it runs headless in tests.

import { logger } from './logger';
import type { SessionStartedPayload } from './types';

const log = logger('session-confirm');

export interface ConfirmActiveSessionDeps {
  /** The id of the session the extension is enforcing, or null. */
  getActiveSessionId: () => Promise<string | null>;
  /** Asks the backend whether the student is still in the session; rejects
   *  when the backend can't be asked. */
  isInSession: (sessionId: string) => Promise<boolean>;
  /** Ends the session locally, exactly as a SessionEnded broadcast would. */
  endSession: (sessionId: string) => Promise<void>;
}

/**
 * Ends the active session if the backend says the student is no longer in it.
 * Only that answer ends it: when the backend can't be asked (the connection
 * dropped again, a backend without the check), the session stays and the next
 * connect asks again.
 */
export async function confirmActiveSession(deps: ConfirmActiveSessionDeps): Promise<void> {
  const sessionId = await deps.getActiveSessionId();
  if (!sessionId) return;

  let stillIn: boolean;
  try {
    stillIn = await deps.isInSession(sessionId);
  } catch (err) {
    log.warn('could not confirm the active session with the backend; keeping it', { sessionId, err });
    return;
  }
  if (stillIn) return;

  log.info('active session ended while the extension was disconnected', { sessionId });
  await deps.endSession(sessionId);
}

export interface CatchUpStartedSessionDeps {
  /** The id of the session the extension is enforcing, or null. */
  getActiveSessionId: () => Promise<string | null>;
  /** Asks the backend for the session the student has been asked into and
   *  hasn't declined or left, or null; rejects when it can't be asked. */
  getStartedSession: () => Promise<SessionStartedPayload | null>;
  /** Starts the session, exactly as a SessionStarted broadcast would. */
  startSession: (payload: SessionStartedPayload) => Promise<void>;
}

/**
 * Starts the session the backend says the student is in when the extension
 * isn't enforcing it yet (#356). The backend answers only once it has added the
 * connection to the student's user group, so between this answer and the
 * SessionStarted broadcasts after it, no start is missed. Run it after
 * confirmActiveSession, so an ended session is ended, and its tabs restored,
 * before a newer one takes its place.
 */
export async function catchUpStartedSession(deps: CatchUpStartedSessionDeps): Promise<void> {
  let started: SessionStartedPayload | null;
  try {
    started = await deps.getStartedSession();
  } catch (err) {
    log.warn('could not ask the backend for a session that started while the extension was not listening', { err });
    return;
  }
  if (!started) return;
  if (started.sessionId === (await deps.getActiveSessionId())) return;

  log.info('session started while the extension was not listening', { sessionId: started.sessionId });
  await deps.startSession(started);
}

export type CatchUpOnConnectDeps = ConfirmActiveSessionDeps & CatchUpStartedSessionDeps;

/**
 * What the extension does each time its hub connection comes up, however it got
 * there: the first start, an automatic reconnect, or a new start after the
 * connection closed (#374). Ends the session the student is no longer in (#354),
 * then starts the one they are in (#356), in that order.
 */
export async function catchUpOnConnect(deps: CatchUpOnConnectDeps): Promise<void> {
  await confirmActiveSession(deps);
  await catchUpStartedSession(deps);
}
