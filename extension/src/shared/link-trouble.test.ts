import { describe, it, expect } from 'vitest';
import {
  LINK_TROUBLE_KEY,
  MAX_ERROR_LENGTH,
  linkTroubleStore,
  mergeTrouble,
  recordFailure,
  type SessionStorageArea,
} from './link-trouble';

describe('recordFailure (#561)', () => {
  it('counts failures from the first one, keeping the latest reason', () => {
    const first = recordFailure(null, 'Specified native messaging host not found.', new Date('2026-11-20T08:00:00Z'));
    const second = recordFailure(first, 'Native host has exited.', new Date('2026-11-20T08:05:00Z'));

    expect(first).toEqual({
      error: 'Specified native messaging host not found.',
      failedAttempts: 1,
      since: '2026-11-20T08:00:00.000Z',
    });
    expect(second).toEqual({ error: 'Native host has exited.', failedAttempts: 2, since: '2026-11-20T08:00:00.000Z' });
  });

  it('says when Edge gave no reason, and caps a long one', () => {
    expect(recordFailure(null, '  ').error).toBe('no reason given');
    const long = recordFailure(null, 'x'.repeat(1000)).error;
    expect(long).toHaveLength(MAX_ERROR_LENGTH + 1);
    expect(long.endsWith('…')).toBe(true);
  });
});

describe('mergeTrouble (#561)', () => {
  it("adds this worker's failures to what an earlier one kept", () => {
    expect(
      mergeTrouble(
        { error: 'old', failedAttempts: 3, since: '2026-11-20T08:00:00.000Z' },
        { error: 'new', failedAttempts: 2, since: '2026-11-20T08:10:00.000Z' },
      ),
    ).toEqual({ error: 'new', failedAttempts: 5, since: '2026-11-20T08:00:00.000Z' });
    const only = { error: 'one', failedAttempts: 1, since: '2026-11-20T08:00:00.000Z' };
    expect(mergeTrouble(null, only)).toBe(only);
    expect(mergeTrouble(only, null)).toBe(only);
    expect(mergeTrouble(null, null)).toBeNull();
  });
});

describe('linkTroubleStore (#561)', () => {
  function fakeStorage(): SessionStorageArea & { items: Record<string, unknown> } {
    const items: Record<string, unknown> = {};
    return {
      items,
      get: async (key) => (key in items ? { [key]: items[key] } : {}),
      set: async (values) => void Object.assign(items, values),
      remove: async (key) => void delete items[key],
    };
  }

  it('keeps the trouble in session storage and removes it once cleared', async () => {
    const storage = fakeStorage();
    const store = linkTroubleStore(storage);
    const trouble = { error: 'Native host has exited.', failedAttempts: 1, since: '2026-11-20T08:00:00.000Z' };

    await store.save(trouble);
    expect(storage.items[LINK_TROUBLE_KEY]).toEqual(trouble);
    expect(await store.load()).toEqual(trouble);

    await store.save(null);
    expect(LINK_TROUBLE_KEY in storage.items).toBe(false);
    expect(await store.load()).toBeNull();
  });

  it('reads anything else, or a storage that fails, as nothing kept', async () => {
    const storage = fakeStorage();
    storage.items[LINK_TROUBLE_KEY] = { error: 42 };
    expect(await linkTroubleStore(storage).load()).toBeNull();

    const failing: SessionStorageArea = {
      get: async () => {
        throw new Error('storage gone');
      },
      set: async () => {
        throw new Error('storage gone');
      },
      remove: async () => {
        throw new Error('storage gone');
      },
    };
    expect(await linkTroubleStore(failing).load()).toBeNull();
    await expect(linkTroubleStore(failing).save(null)).resolves.toBeUndefined();
  });
});
