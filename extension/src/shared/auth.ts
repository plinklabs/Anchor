// Production student authentication (#289).
//
// In release the extension authenticates to the SignalR hub with a real Entra
// access token instead of the dev-only dev_impersonate_oid shortcut. It can't use
// the Windows WAM broker the on-box agent uses (that's native, not available to an
// extension), so it rides Edge's existing Office 365 session via the OAuth2
// implicit flow through chrome.identity.launchWebAuthFlow:
//
//   launchWebAuthFlow → login.microsoftonline.com/<tenant>/oauth2/v2.0/authorize
//                       (inside Edge, where the student is already signed in)
//                     → redirect to https://<ext-id>.chromiumapp.org/#access_token=…
//
// We use the IMPLICIT flow (response_type=token, response_mode=fragment) rather
// than auth-code+PKCE deliberately: the token comes back in the redirect fragment,
// so there is no token-endpoint call and thus no CORS problem (AAD's SPA token
// endpoint validates Origin against the registered redirect URI, which never
// matches an extension's chrome-extension:// origin). The deployment's app
// registration must therefore have the chromiumapp.org redirect URI as a Web
// platform with access-token implicit issuance enabled (wired by setup.ps1).
//
// Silent-first: with the school account already signed into Edge, an
// interactive:false / prompt=none flow returns a token with no UI. We only fall
// back to an interactive flow when there is no live session / consent yet.
//
// The pure URL-building and redirect-parsing helpers are exported and unit-tested;
// the chrome.identity wiring is injected so the authenticator runs headless.

import { logger } from './logger';
import type { AuthConfig } from './settings';

const log = logger('auth');

/** Seconds of headroom before real expiry at which we treat a token as stale. */
const EXPIRY_SKEW_MS = 60_000;

/** A token plus the absolute epoch-ms at which it should be considered expired. */
export interface CachedToken {
  accessToken: string;
  expiresAt: number;
}

/** The access token and its lifetime (ms) parsed out of an implicit redirect. */
export interface ParsedToken {
  accessToken: string;
  expiresInMs: number;
}

/**
 * Build the Entra v2.0 authorize URL for the implicit access-token flow.
 * `silent` adds `prompt=none` so the flow completes against an existing session
 * with no UI (the common case on a school-managed Edge).
 */
export function buildAuthorizeUrl(
  config: AuthConfig,
  redirectUri: string,
  opts: { state: string; silent: boolean },
): string {
  const url = new URL(`https://login.microsoftonline.com/${encodeURIComponent(config.tenantId)}/oauth2/v2.0/authorize`);
  url.searchParams.set('client_id', config.clientId);
  url.searchParams.set('response_type', 'token');
  url.searchParams.set('redirect_uri', redirectUri);
  url.searchParams.set('scope', config.scope);
  url.searchParams.set('response_mode', 'fragment');
  url.searchParams.set('state', opts.state);
  if (opts.silent) url.searchParams.set('prompt', 'none');
  return url.toString();
}

/**
 * Parse the access token out of the redirect URL launchWebAuthFlow resolves with.
 * The implicit flow returns its params in the URL fragment
 * (`#access_token=…&expires_in=…&state=…`), or an error fragment
 * (`#error=…&error_description=…`). Throws on an AAD error, a state mismatch, or a
 * missing token so a malformed redirect can never masquerade as a valid token.
 */
export function parseImplicitRedirect(redirectUrl: string, expectedState: string): ParsedToken {
  const hashIndex = redirectUrl.indexOf('#');
  const fragment = hashIndex >= 0 ? redirectUrl.slice(hashIndex + 1) : '';
  const params = new URLSearchParams(fragment);

  const error = params.get('error');
  if (error) {
    const description = params.get('error_description') ?? '';
    const suffix = description ? ` — ${description}` : '';
    throw new Error(`Entra auth error: ${error}${suffix}`);
  }

  // A mismatched state means the redirect didn't come from the request we made —
  // reject it rather than trust a token we can't attribute to our own flow.
  const state = params.get('state');
  if (state !== expectedState) {
    throw new Error('Entra auth error: redirect state did not match the request.');
  }

  const accessToken = params.get('access_token');
  if (!accessToken) {
    throw new Error('Entra auth error: redirect carried no access_token.');
  }

  // expires_in is seconds from now; default to a conservative 1h if absent.
  const expiresInSec = Number.parseInt(params.get('expires_in') ?? '', 10);
  const expiresInMs = Number.isFinite(expiresInSec) && expiresInSec > 0 ? expiresInSec * 1000 : 3_600_000;
  return { accessToken, expiresInMs };
}

export interface AuthenticatorDeps {
  /** Opens the web auth flow; defaults to chrome.identity.launchWebAuthFlow (promise form). */
  launchWebAuthFlow?: (details: { url: string; interactive: boolean }) => Promise<string | undefined>;
  /** The extension's auth redirect URL; defaults to chrome.identity.getRedirectURL(). */
  getRedirectUrl?: () => string;
  /** Current epoch-ms; injected for tests. */
  now?: () => number;
  /** Opaque per-request state value; injected for tests. */
  makeState?: () => string;
}

/**
 * Acquires + caches an Entra access token for the hub, refreshing silently when it
 * expires. One instance per auth config; rebuilt when the agent hands down a new
 * config. Concurrent callers (SignalR negotiate + a reconnect, say) share a single
 * in-flight acquisition rather than racing two auth windows.
 */
export class EntraAuthenticator {
  private readonly config: AuthConfig;
  private readonly launch: (details: { url: string; interactive: boolean }) => Promise<string | undefined>;
  private readonly getRedirectUrl: () => string;
  private readonly now: () => number;
  private readonly makeState: () => string;

  private cached: CachedToken | null = null;
  private inflight: Promise<string> | null = null;

  constructor(config: AuthConfig, deps: AuthenticatorDeps = {}) {
    this.config = config;
    this.launch = deps.launchWebAuthFlow
      ?? ((details) => chrome.identity.launchWebAuthFlow(details) as Promise<string | undefined>);
    this.getRedirectUrl = deps.getRedirectUrl ?? (() => chrome.identity.getRedirectURL());
    this.now = deps.now ?? (() => Date.now());
    this.makeState = deps.makeState ?? (() => crypto.randomUUID());
  }

  /**
   * Return a valid access token, acquiring or refreshing one if needed.
   * `allowInteractive` lets the caller permit a sign-in window: the initial
   * connect passes true (a one-time prompt if SSO can't satisfy it silently);
   * background refreshes (SignalR's accessTokenFactory) pass false so a reconnect
   * never pops an unexpected window — a failed silent refresh just fails the
   * connection, which retries.
   */
  async getToken(opts: { allowInteractive: boolean }): Promise<string> {
    if (this.cached && this.cached.expiresAt - this.now() > EXPIRY_SKEW_MS) {
      return this.cached.accessToken;
    }
    if (this.inflight !== null) return this.inflight;

    this.inflight = this.acquire(opts.allowInteractive)
      .then((token) => {
        this.cached = token;
        return token.accessToken;
      })
      .finally(() => {
        this.inflight = null;
      });
    return this.inflight;
  }

  /** Drop the cached token so the next getToken re-acquires (e.g. after a 401). */
  invalidate(): void {
    this.cached = null;
  }

  private async acquire(allowInteractive: boolean): Promise<CachedToken> {
    try {
      return await this.runFlow(false);
    } catch (err) {
      if (!allowInteractive) throw err;
      // No live session / consent yet — fall back to a visible sign-in once.
      log.info('silent token acquisition failed; falling back to interactive sign-in', err);
      return this.runFlow(true);
    }
  }

  private async runFlow(interactive: boolean): Promise<CachedToken> {
    const state = this.makeState();
    const url = buildAuthorizeUrl(this.config, this.getRedirectUrl(), { state, silent: !interactive });
    const redirect = await this.launch({ url, interactive });
    if (!redirect) {
      // launchWebAuthFlow resolves with undefined when the flow produced no
      // redirect (e.g. the window was closed) — treat as an acquisition failure.
      throw new Error('Entra auth flow returned no redirect.');
    }
    const parsed = parseImplicitRedirect(redirect, state);
    return { accessToken: parsed.accessToken, expiresAt: this.now() + parsed.expiresInMs };
  }
}
