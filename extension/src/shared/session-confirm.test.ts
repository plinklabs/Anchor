import { describe, it, expect } from 'vitest';
import { confirmActiveSession } from './session-confirm';

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
