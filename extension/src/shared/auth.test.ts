import { describe, it, expect, vi } from 'vitest';
import {
  buildAuthorizeUrl,
  parseImplicitRedirect,
  EntraAuthenticator,
} from './auth';
import type { AuthConfig } from './settings';

const CONFIG: AuthConfig = {
  tenantId: 'tenant-1',
  clientId: 'client-1',
  scope: 'api://api-app/access_as_user',
};
const REDIRECT_URI = 'https://ext-id.chromiumapp.org/';

describe('buildAuthorizeUrl', () => {
  it('targets the tenant authorize endpoint with the implicit access-token params', () => {
    const url = new URL(buildAuthorizeUrl(CONFIG, REDIRECT_URI, { state: 'st-1', silent: false }));

    expect(url.origin + url.pathname).toBe(
      'https://login.microsoftonline.com/tenant-1/oauth2/v2.0/authorize',
    );
    expect(url.searchParams.get('client_id')).toBe('client-1');
    expect(url.searchParams.get('response_type')).toBe('token');
    expect(url.searchParams.get('redirect_uri')).toBe(REDIRECT_URI);
    expect(url.searchParams.get('scope')).toBe('api://api-app/access_as_user');
    expect(url.searchParams.get('response_mode')).toBe('fragment');
    expect(url.searchParams.get('state')).toBe('st-1');
    // Interactive request: no prompt=none.
    expect(url.searchParams.get('prompt')).toBeNull();
  });

  it('adds prompt=none for a silent request (rides the existing Edge session)', () => {
    const url = new URL(buildAuthorizeUrl(CONFIG, REDIRECT_URI, { state: 'st-1', silent: true }));
    expect(url.searchParams.get('prompt')).toBe('none');
  });
});

describe('parseImplicitRedirect', () => {
  it('extracts the access token and lifetime from the redirect fragment', () => {
    const redirect = `${REDIRECT_URI}#access_token=abc.def&expires_in=3600&state=st-1&token_type=Bearer`;
    const parsed = parseImplicitRedirect(redirect, 'st-1');
    expect(parsed.accessToken).toBe('abc.def');
    expect(parsed.expiresInMs).toBe(3_600_000);
  });

  it('defaults the lifetime to 1h when expires_in is absent', () => {
    const redirect = `${REDIRECT_URI}#access_token=abc&state=st-1`;
    expect(parseImplicitRedirect(redirect, 'st-1').expiresInMs).toBe(3_600_000);
  });

  it('throws on an AAD error fragment, surfacing the description', () => {
    const redirect = `${REDIRECT_URI}#error=login_required&error_description=Need+interactive&state=st-1`;
    expect(() => parseImplicitRedirect(redirect, 'st-1')).toThrow(/login_required.*Need interactive/);
  });

  it('throws on a state mismatch (a redirect not from our request)', () => {
    const redirect = `${REDIRECT_URI}#access_token=abc&state=other`;
    expect(() => parseImplicitRedirect(redirect, 'st-1')).toThrow(/state did not match/);
  });

  it('throws when no access_token is present', () => {
    const redirect = `${REDIRECT_URI}#state=st-1`;
    expect(() => parseImplicitRedirect(redirect, 'st-1')).toThrow(/no access_token/);
  });
});

/** Build an authenticator whose chrome deps are fully faked + a controllable clock. */
function makeAuthenticator(
  launch: (details: { url: string; interactive: boolean }) => Promise<string | undefined>,
  nowRef = { value: 1_000_000 },
) {
  let n = 0;
  const auth = new EntraAuthenticator(CONFIG, {
    launchWebAuthFlow: launch,
    getRedirectUrl: () => REDIRECT_URI,
    now: () => nowRef.value,
    makeState: () => `state-${n++}`,
  });
  return { auth, nowRef };
}

/**
 * Build a redirect URL echoing the state out of the authorize URL the flow
 * actually requested — the authenticator generates a fresh state per flow and
 * verifies it on return, so a fixed state would spuriously fail the 2nd flow.
 */
function redirectEchoingState(requestedUrl: string, token = 'tok', expiresIn = 3600): string {
  const state = new URL(requestedUrl).searchParams.get('state') ?? '';
  return `${REDIRECT_URI}#access_token=${token}&expires_in=${expiresIn}&state=${state}`;
}

describe('EntraAuthenticator', () => {
  it('acquires a token silently (interactive:false) when SSO satisfies it', async () => {
    const launch = vi.fn(async (d: { url: string; interactive: boolean }) => {
      expect(d.interactive).toBe(false);
      return redirectEchoingState(d.url);
    });
    const { auth } = makeAuthenticator(launch);

    expect(await auth.getToken({ allowInteractive: true })).toBe('tok');
    expect(launch).toHaveBeenCalledTimes(1);
  });

  it('falls back to an interactive flow when the silent flow fails and it is allowed', async () => {
    const launch = vi.fn(async (d: { url: string; interactive: boolean }) => {
      if (!d.interactive) throw new Error('login_required');
      return redirectEchoingState(d.url, 'interactive-tok');
    });
    const { auth } = makeAuthenticator(launch);

    expect(await auth.getToken({ allowInteractive: true })).toBe('interactive-tok');
    expect(launch).toHaveBeenCalledTimes(2); // silent, then interactive
    expect(launch.mock.calls.map((c) => c[0].interactive)).toEqual([false, true]);
  });

  it('does NOT pop interactive when not allowed — a failed silent refresh just throws', async () => {
    const launch = vi.fn(async () => {
      throw new Error('login_required');
    });
    const { auth } = makeAuthenticator(launch);

    await expect(auth.getToken({ allowInteractive: false })).rejects.toThrow(/login_required/);
    expect(launch).toHaveBeenCalledTimes(1); // silent only, no interactive popup
  });

  it('caches the token and does not re-acquire while it is still fresh', async () => {
    const launch = vi.fn(async (d: { url: string }) => redirectEchoingState(d.url));
    const { auth } = makeAuthenticator(launch);

    await auth.getToken({ allowInteractive: false });
    await auth.getToken({ allowInteractive: false });
    expect(launch).toHaveBeenCalledTimes(1);
  });

  it('re-acquires once the token is within the expiry skew', async () => {
    const launch = vi.fn(async (d: { url: string }) => redirectEchoingState(d.url, 'tok', 3600));
    const { auth, nowRef } = makeAuthenticator(launch);

    await auth.getToken({ allowInteractive: false });
    // Advance to inside the 60s skew before the 1h expiry → stale → re-acquire.
    nowRef.value += 3_600_000 - 30_000;
    await auth.getToken({ allowInteractive: false });
    expect(launch).toHaveBeenCalledTimes(2);
  });

  it('coalesces concurrent acquisitions into a single flow', async () => {
    let resolve!: (v: string) => void;
    let requestedUrl = '';
    const launch = vi.fn((d: { url: string }) => {
      requestedUrl = d.url;
      return new Promise<string>((r) => { resolve = r; });
    });
    const { auth } = makeAuthenticator(launch);

    const a = auth.getToken({ allowInteractive: false });
    const b = auth.getToken({ allowInteractive: false });
    resolve(redirectEchoingState(requestedUrl));
    expect(await a).toBe('tok');
    expect(await b).toBe('tok');
    expect(launch).toHaveBeenCalledTimes(1);
  });

  it('invalidate() forces the next getToken to re-acquire', async () => {
    const launch = vi.fn(async (d: { url: string }) => redirectEchoingState(d.url));
    const { auth } = makeAuthenticator(launch);

    await auth.getToken({ allowInteractive: false });
    auth.invalidate();
    await auth.getToken({ allowInteractive: false });
    expect(launch).toHaveBeenCalledTimes(2);
  });
});
