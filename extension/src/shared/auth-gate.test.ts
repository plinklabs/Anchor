import { describe, it, expect, beforeEach, vi } from 'vitest';
import {
  AUTH_GATE_KEY,
  backoffMs,
  claimInteractiveSignIn,
  clearAuthGate,
  describeAuthFailure,
  getAuthFailure,
  planInteractiveClaim,
  readAuthGate,
  recordAuthFailure,
  type AuthGateState,
} from './auth-gate';

// #331. The regression these lock down: the interactive sign-in guard used to be
// a module-level flag in the MV3 service worker, so every worker revival handed
// a permanently-broken auth config one more sign-in window. The gate here is
// storage-backed, so a *fresh read* — which is what a revived worker does — must
// still refuse, and the refusal must widen with each attempt.

/** In-memory chrome.storage.session, mirroring session-state.test.ts. */
function installChromeStorageMock(): Record<string, unknown> {
  const store: Record<string, unknown> = {};
  vi.stubGlobal('chrome', {
    storage: {
      session: {
        get: async (key: string) => ({ [key]: store[key] }),
        set: async (items: Record<string, unknown>) => {
          Object.assign(store, items);
        },
        remove: async (key: string) => {
          delete store[key];
        },
      },
    },
  });
  return store;
}

describe('backoffMs', () => {
  it('widens with each consecutive attempt and then plateaus', () => {
    expect(backoffMs(1)).toBe(2 * 60_000);
    expect(backoffMs(2)).toBe(10 * 60_000);
    expect(backoffMs(3)).toBe(30 * 60_000);
    expect(backoffMs(4)).toBe(60 * 60_000);
    // Beyond the ladder it stays at the cap rather than growing forever.
    expect(backoffMs(9)).toBe(60 * 60_000);
  });

  it('is zero before any attempt has been made', () => {
    expect(backoffMs(0)).toBe(0);
  });
});

describe('planInteractiveClaim', () => {
  const fresh: AuthGateState = { attempts: 0, nextAttemptAt: 0 };

  it('allows the first sign-in and schedules the next window', () => {
    const { allowed, next } = planInteractiveClaim(fresh, 1_000);
    expect(allowed).toBe(true);
    expect(next.attempts).toBe(1);
    expect(next.nextAttemptAt).toBe(1_000 + 2 * 60_000);
  });

  it('refuses while the backoff from the previous attempt is still running', () => {
    const { next } = planInteractiveClaim(fresh, 1_000);
    // A revived worker re-reads the same state — it must not get a free window.
    const second = planInteractiveClaim(next, 1_000 + 30_000);
    expect(second.allowed).toBe(false);
    expect(second.next).toBe(next);
  });

  it('allows again once the backoff has elapsed, with a wider next window', () => {
    const first = planInteractiveClaim(fresh, 0).next;
    const second = planInteractiveClaim(first, 2 * 60_000);
    expect(second.allowed).toBe(true);
    expect(second.next.attempts).toBe(2);
    expect(second.next.nextAttemptAt).toBe(2 * 60_000 + 10 * 60_000);
  });

  it('keeps the recorded failure across a claim', () => {
    const withFailure: AuthGateState = {
      ...fresh,
      failure: { code: 'AADSTS700051', message: 'nope', at: 5 },
    };
    expect(planInteractiveClaim(withFailure, 10).next.failure).toEqual(withFailure.failure);
  });
});

describe('describeAuthFailure', () => {
  it('pulls the AADSTS code out of an Entra error description', () => {
    const failure = describeAuthFailure(
      new Error(
        "Entra auth error: unsupported_response_type — AADSTS700051: response_type 'token' is not enabled for the application.",
      ),
      1234,
    );
    expect(failure.code).toBe('AADSTS700051');
    expect(failure.message).toContain('response_type');
    expect(failure.at).toBe(1234);
  });

  it('records a failure with no Entra code (a dead authority, say) as code-less', () => {
    const failure = describeAuthFailure(new Error('Authorization page could not be loaded.'));
    expect(failure.code).toBeNull();
    expect(failure.message).toBe('Authorization page could not be loaded.');
  });

  it('survives a non-Error rejection', () => {
    expect(describeAuthFailure('boom').message).toBe('boom');
    expect(describeAuthFailure(undefined).message).toBe('undefined');
  });
});

describe('the storage-backed gate (survives a worker restart)', () => {
  let store: Record<string, unknown>;
  beforeEach(() => {
    store = installChromeStorageMock();
  });

  it('allows one interactive sign-in and then refuses a freshly-read gate', async () => {
    expect(await claimInteractiveSignIn(1_000)).toBe(true);
    // Everything module-level is gone after a worker restart; only storage is
    // left, and that is what must refuse. Pre-#331 this returned true forever.
    expect(await claimInteractiveSignIn(1_000 + 1)).toBe(false);
    expect(await claimInteractiveSignIn(1_000 + 60_000)).toBe(false);
  });

  it('lets a new attempt through once the backoff has expired', async () => {
    await claimInteractiveSignIn(0);
    expect(await claimInteractiveSignIn(2 * 60_000)).toBe(true);
    // ...and the next refusal window is the wider one.
    expect(await claimInteractiveSignIn(2 * 60_000 + 9 * 60_000)).toBe(false);
    expect(await claimInteractiveSignIn(2 * 60_000 + 10 * 60_000)).toBe(true);
  });

  it('persists the gate under the storage.session key', async () => {
    await claimInteractiveSignIn(500);
    expect(store[AUTH_GATE_KEY]).toMatchObject({ attempts: 1, nextAttemptAt: 500 + 2 * 60_000 });
  });

  it('records a failure alongside the attempt state and exposes it to the UI', async () => {
    await claimInteractiveSignIn(0);
    const recorded = await recordAuthFailure(new Error('AADSTS700051: nope'), 42);
    expect(recorded.code).toBe('AADSTS700051');

    const gate = await readAuthGate();
    expect(gate.attempts).toBe(1); // recording must not reset the backoff
    expect(gate.failure).toEqual(recorded);
    expect(await getAuthFailure()).toEqual(recorded);
  });

  it('clears everything on success, so the next connect may prompt again', async () => {
    await claimInteractiveSignIn(0);
    await recordAuthFailure(new Error('AADSTS700051: nope'), 1);
    await clearAuthGate();

    expect(await getAuthFailure()).toBeNull();
    expect(await claimInteractiveSignIn(1_000)).toBe(true);
  });

  it('treats a malformed stored gate as no gate at all', async () => {
    store[AUTH_GATE_KEY] = { attempts: 'lots', nextAttemptAt: null, failure: 7 };
    const gate = await readAuthGate();
    expect(gate).toEqual({ attempts: 0, nextAttemptAt: 0 });
    expect(await claimInteractiveSignIn(1_000)).toBe(true);
  });

  it('does not open a window when storage cannot be read at all', async () => {
    // A rejecting store must not throw out of the gate — the connect attempt
    // still has to proceed (silently) rather than crash the worker.
    vi.stubGlobal('chrome', {
      storage: {
        session: {
          get: async () => {
            throw new Error('no storage here');
          },
          set: async () => {
            throw new Error('no storage here');
          },
          remove: async () => {
            throw new Error('no storage here');
          },
        },
      },
    });
    await expect(claimInteractiveSignIn(1_000)).resolves.toBe(true);
    await expect(getAuthFailure()).resolves.toBeNull();
    await expect(clearAuthGate()).resolves.toBeUndefined();
  });
});
