import { describe, it, expect } from 'vitest';
import { catchUpStartedSession, confirmActiveSession } from './session-confirm';
import type { SessionStartedPayload } from './types';

function makeDeps(options: {
  activeSessionId: string | null;
  isInSession?: (sessionId: string) => Promise<boolean>;
}) {
  const asked: string[] = [];
  const ended: string[] = [];
  return {
    asked,
    ended,
    deps: {
      getActiveSessionId: async () => options.activeSessionId,
      isInSession: async (sessionId: string) => {
        asked.push(sessionId);
        return (options.isInSession ?? (async () => true))(sessionId);
      },
      endSession: async (sessionId: string) => {
        ended.push(sessionId);
      },
    },
  };
}

describe('confirmActiveSession', () => {
  it('does not ask the backend when no session is active', async () => {
    const h = makeDeps({ activeSessionId: null });

    await confirmActiveSession(h.deps);

    expect(h.asked).toEqual([]);
    expect(h.ended).toEqual([]);
  });

  it('keeps a session the student is still in', async () => {
    const h = makeDeps({ activeSessionId: 'sess-1', isInSession: async () => true });

    await confirmActiveSession(h.deps);

    expect(h.asked).toEqual(['sess-1']);
    expect(h.ended).toEqual([]);
  });

  it('ends a session that ended while the extension was disconnected (#354)', async () => {
    const h = makeDeps({ activeSessionId: 'sess-1', isInSession: async () => false });

    await confirmActiveSession(h.deps);

    expect(h.ended).toEqual(['sess-1']);
  });

  it('keeps the session when the backend cannot be asked', async () => {
    const h = makeDeps({
      activeSessionId: 'sess-1',
      isInSession: async () => {
        throw new Error('connection dropped again');
      },
    });

    await confirmActiveSession(h.deps);

    expect(h.asked).toEqual(['sess-1']);
    expect(h.ended).toEqual([]);
  });
});

function startedPayload(sessionId: string): SessionStartedPayload {
  return {
    sessionId,
    classId: 'class-1',
    startedAt: '2026-10-01T08:00:00Z',
    joinCode: '123456',
    apps: [],
    domains: [{ matchType: 'Suffix', value: 'example.org' }],
  };
}

function makeCatchUpDeps(options: {
  activeSessionId: string | null;
  getStartedSession: () => Promise<SessionStartedPayload | null>;
}) {
  const started: SessionStartedPayload[] = [];
  return {
    started,
    deps: {
      getActiveSessionId: async () => options.activeSessionId,
      getStartedSession: options.getStartedSession,
      startSession: async (payload: SessionStartedPayload) => {
        started.push(payload);
      },
    },
  };
}

describe('catchUpStartedSession', () => {
  it('starts a session that started while the extension was not listening (#356)', async () => {
    const payload = startedPayload('sess-1');
    const h = makeCatchUpDeps({ activeSessionId: null, getStartedSession: async () => payload });

    await catchUpStartedSession(h.deps);

    expect(h.started).toEqual([payload]);
  });

  it('replaces an older session with the one the student is in now', async () => {
    const payload = startedPayload('sess-2');
    const h = makeCatchUpDeps({ activeSessionId: 'sess-1', getStartedSession: async () => payload });

    await catchUpStartedSession(h.deps);

    expect(h.started).toEqual([payload]);
  });

  it('leaves the session it already enforces alone', async () => {
    // Restarting it would drop the unblock grants merged into the cached
    // allowlist since it started.
    const h = makeCatchUpDeps({
      activeSessionId: 'sess-1',
      getStartedSession: async () => startedPayload('sess-1'),
    });

    await catchUpStartedSession(h.deps);

    expect(h.started).toEqual([]);
  });

  it('starts nothing when no session runs or the backend cannot be asked', async () => {
    const none = makeCatchUpDeps({ activeSessionId: null, getStartedSession: async () => null });
    const failing = makeCatchUpDeps({
      activeSessionId: null,
      getStartedSession: async () => {
        throw new Error('connection dropped again');
      },
    });

    await catchUpStartedSession(none.deps);
    await catchUpStartedSession(failing.deps);

    expect(none.started).toEqual([]);
    expect(failing.started).toEqual([]);
  });
});
