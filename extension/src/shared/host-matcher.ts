// Pure host-match logic for the session allowlist. No DOM, no chrome APIs —
// runnable in vanilla Node so it can be unit-tested without a browser harness.
// Mirrors the wire shape produced by the backend's SessionAllowlistExpander
// (#70): each entry carries a MatchType string and a value, and the
// MatchType vocabulary tracks BundleEntryMatchType / AllowedDomainMatchTypes
// so the agent and the extension speak the same dialect.

/**
 * The match-type values the backend emits in AllowedDomainDto.MatchType.
 * Kept as a string union (not an enum) because the wire format is strings;
 * an enum would force a translation layer on every payload.
 */
export type DomainMatchType = 'Exact' | 'Wildcard' | 'Suffix';

export interface AllowedDomain {
  matchType: DomainMatchType;
  value: string;
}

/**
 * Returns true if the given URL's hostname is allowed by any of the rules.
 *
 * Rules:
 * - `Exact`    — case-insensitive hostname equality.
 * - `Wildcard` — the rule value is expected to be `*.<suffix>`; matches the
 *                literal `<suffix>` AND any subdomain `*.<suffix>`. We accept
 *                bare `<suffix>` too (treated the same) so a misconfigured
 *                catalogue entry can't silently fail open or closed.
 * - `Suffix`   — same semantics as Wildcard but the value may or may not
 *                start with `*.`; either form matches `<suffix>` and any
 *                subdomain.
 *
 * Non-http(s) URLs (chrome:, edge:, file:, about:, javascript:, data:, ...) are
 * allowed: they never represent navigable user content the extension should
 * police, and trying to block them would only break the browser chrome itself.
 *
 * The empty allowlist case is handled by the caller — when there's no active
 * session, this function isn't called at all. When a session IS active and
 * carries an empty allowlist, the baseline (#70) has already merged in the
 * always-allowed domains server-side, so the empty case here means "block".
 */
export function isUrlAllowed(url: string, rules: ReadonlyArray<AllowedDomain>): boolean {
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    // Unparseable URL — treat as not-a-navigation. Filtering only protects
    // http(s) anyway; everything else (about:blank, javascript:, ...) should
    // pass through to whatever the browser would normally do.
    return true;
  }

  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
    return true;
  }

  const hostname = parsed.hostname.toLowerCase();
  if (hostname.length === 0) {
    return true;
  }

  for (const rule of rules) {
    if (matches(hostname, rule)) {
      return true;
    }
  }
  return false;
}

function matches(hostname: string, rule: AllowedDomain): boolean {
  const raw = rule.value?.trim().toLowerCase();
  if (!raw) return false;

  switch (rule.matchType) {
    case 'Exact':
      return hostname === raw;

    case 'Wildcard':
    case 'Suffix': {
      // Accept the canonical `*.foo.com` form, but also a bare `foo.com`
      // (catalogue authors do both in practice; the backend doesn't enforce
      // the leading `*.`). Both should match `foo.com` AND any subdomain.
      const suffix = raw.startsWith('*.') ? raw.slice(2) : raw;
      if (suffix.length === 0) return false;
      if (hostname === suffix) return true;
      return hostname.endsWith('.' + suffix);
    }

    default:
      return false;
  }
}
