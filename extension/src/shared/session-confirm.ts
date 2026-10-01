// Catching up on a session that ended while the extension was offline (#354).
//
// The backend tells clients that a session ended with a SessionEnded broadcast,
// and a broadcast reaches only the hub connections open when it is sent. The
// extension's connection is replaced all the time — every service-worker
// revival opens a new one, and a network drop or a laptop going to sleep forces
// a reconnect — so a session can end while the extension isn't there to hear
// it. It would then keep blocking against the ended session's allowlist until
// the browser restarted. So each time the hub connects, the extension asks the
// backend whether the student is still in the session it is enforcing, and
// ends it if not. Same injected-deps shape as SessionHeartbeat so it runs
// headless in tests.

import { logger } from './logger';

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
