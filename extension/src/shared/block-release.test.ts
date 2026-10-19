import { describe, it, expect } from 'vitest';
import { isBlockLifted, type BlockedPage } from './block-release';
import type { AllowedDomain } from './host-matcher';

const SESSION = 'session-123';

const page = (blockedUrl: string, sessionId: string | null = SESSION): BlockedPage => ({
  blockedUrl,
  sessionId,
});

const wildcard = (value: string): AllowedDomain => ({ matchType: 'Wildcard', value });
const suffix = (value: string): AllowedDomain => ({ matchType: 'Suffix', value });
const exact = (value: string): AllowedDomain => ({ matchType: 'Exact', value });

// The allowlist a bundle change sends (#426): the baseline plus the bundle's
// entries, which arrive in their catalogue form, `*.` prefix included.
const withSmartschoolBundle: AllowedDomain[] = [
  wildcard('*.microsoftonline.com'),
  wildcard('*.office.com'),
  wildcard('*.smartschool.be'),
];

describe('isBlockLifted — a bundle change (#426)', () => {
  it('lifts a block on a subdomain the bundle covers through a wildcard entry', () => {
    // The pilot case: a school's Smartschool tab, blocked by the scan at the
    // start of a session that had no bundles yet.
    expect(
      isBlockLifted(page('https://school.smartschool.be/index.php'), SESSION, withSmartschoolBundle),
    ).toBe(true);
  });

  it('lifts a block on the bare domain of a wildcard entry', () => {
    expect(isBlockLifted(page('https://smartschool.be/'), SESSION, withSmartschoolBundle)).toBe(true);
  });

  it('keeps a block the new allowlist does not cover', () => {
    expect(isBlockLifted(page('https://coolmathgames.com/'), SESSION, withSmartschoolBundle)).toBe(
      false,
    );
  });

  it('keeps a block on a look-alike host that only ends in the same letters', () => {
    expect(
      isBlockLifted(page('https://notsmartschool.be/'), SESSION, withSmartschoolBundle),
    ).toBe(false);
  });

  it('keeps the block once the bundle is removed again', () => {
    const baselineOnly = withSmartschoolBundle.filter((d) => d.value !== '*.smartschool.be');
    expect(
      isBlockLifted(page('https://school.smartschool.be/'), SESSION, baselineOnly),
    ).toBe(false);
  });

  it('lifts a block on an exact host, but not on a subdomain of it', () => {
    // The block page used to treat every host as a suffix. With an Exact entry
    // that would send the tab to a page the filter blocks again at once.
    const domains = [exact('outlook.office365.com')];
    expect(isBlockLifted(page('https://outlook.office365.com/mail'), SESSION, domains)).toBe(true);
    expect(isBlockLifted(page('https://eu.outlook.office365.com/mail'), SESSION, domains)).toBe(
      false,
    );
  });
});

describe('isBlockLifted — an approved unblock request (#73)', () => {
  it('lifts a block on the granted host and its subdomains (Suffix grant)', () => {
    const domains = [suffix('reddit.com')];
    expect(isBlockLifted(page('https://reddit.com/r/learnpython'), SESSION, domains)).toBe(true);
    expect(isBlockLifted(page('https://www.reddit.com/'), SESSION, domains)).toBe(true);
  });

  it('matches hosts case-insensitively', () => {
    expect(isBlockLifted(page('https://WWW.Reddit.com/'), SESSION, [suffix('reddit.com')])).toBe(true);
  });
});

describe('isBlockLifted — what it never lifts', () => {
  it('ignores an allowlist for another session', () => {
    expect(
      isBlockLifted(page('https://school.smartschool.be/'), 'session-OTHER', withSmartschoolBundle),
    ).toBe(false);
  });

  it('ignores an allowlist when the page has no session', () => {
    expect(
      isBlockLifted(page('https://school.smartschool.be/', null), SESSION, withSmartschoolBundle),
    ).toBe(false);
  });

  it('keeps the block when the blocked url is missing or junk', () => {
    expect(isBlockLifted(page(''), SESSION, withSmartschoolBundle)).toBe(false);
    expect(isBlockLifted(page('not a url'), SESSION, withSmartschoolBundle)).toBe(false);
  });

  it('keeps the block on a non-web url, which the matcher would wave through', () => {
    expect(isBlockLifted(page('javascript:alert(1)'), SESSION, withSmartschoolBundle)).toBe(false);
    expect(isBlockLifted(page('chrome://settings'), SESSION, withSmartschoolBundle)).toBe(false);
  });

  it('keeps the block when the allowlist is empty', () => {
    expect(isBlockLifted(page('https://school.smartschool.be/'), SESSION, [])).toBe(false);
  });
});
