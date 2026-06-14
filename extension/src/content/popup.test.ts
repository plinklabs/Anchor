import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { allowedSiteLabels } from './popup';
import type { ActiveSessionState } from '../shared/types';

// AE2 (#178): the toolbar-action status popup. Two layers of guard:
//  1. the pure list-derivation logic (allowedSiteLabels), and
//  2. the popup.html ink-treatment markup, mirroring block-page.test.ts — so a
//     regression that drops the ink pin or the per-product accent fails here.

const html = readFileSync(
  fileURLToPath(new URL('./popup.html', import.meta.url)),
  'utf8',
);

function session(partial: Partial<ActiveSessionState> = {}): ActiveSessionState {
  return {
    sessionId: 's1',
    classId: 'c1',
    joinCode: 'AB12',
    startedAt: '2026-06-14T10:00:00Z',
    domains: [],
    ...partial,
  };
}

describe('allowedSiteLabels', () => {
  it('returns the deduped, sorted hostnames from the session allowlist', () => {
    const state = session({
      domains: [
        { matchType: 'Suffix', value: 'wikipedia.org' },
        { matchType: 'Exact', value: 'docs.google.com' },
        { matchType: 'Suffix', value: 'wikipedia.org' }, // dup
      ],
    });
    expect(allowedSiteLabels(state)).toEqual(['docs.google.com', 'wikipedia.org']);
  });

  it('normalises case and trims whitespace before deduping', () => {
    const state = session({
      domains: [
        { matchType: 'Suffix', value: '  Reddit.com ' },
        { matchType: 'Suffix', value: 'reddit.com' },
      ],
    });
    expect(allowedSiteLabels(state)).toEqual(['reddit.com']);
  });

  it('drops empty / whitespace-only entries', () => {
    const state = session({
      domains: [
        { matchType: 'Suffix', value: '' },
        { matchType: 'Suffix', value: '   ' },
        { matchType: 'Suffix', value: 'khanacademy.org' },
      ],
    });
    expect(allowedSiteLabels(state)).toEqual(['khanacademy.org']);
  });

  it('returns an empty list for a baseline-only (no-domain) session', () => {
    expect(allowedSiteLabels(session({ domains: [] }))).toEqual([]);
  });
});

describe('popup page — fixed ink treatment (AE2 / #178)', () => {
  it('hard-wears the DS ink treatment on <body>', () => {
    // `.plink-ink` is a class on the element, not a media query — that is what
    // makes the surface fixed ink on every machine.
    expect(html).toMatch(/<body[^>]*class="[^"]*\bplink-ink\b[^"]*"/);
  });

  it('pins color-scheme to dark so native controls/scrollbars stay ink', () => {
    expect(html).toMatch(/color-scheme:\s*dark/);
  });

  it('never follows the OS theme (no prefers-color-scheme media swap)', () => {
    expect(html).not.toMatch(/@media[^{]*prefers-color-scheme/);
  });

  it('carries the one per-product identity rule in Anchor indigo', () => {
    expect(html).toMatch(/class="pl-identity-rule"/);
    expect(html).toMatch(/--product-accent:\s*#7e80d2/i);
  });

  it('wears the signature concentric-ring ping as the focus-session mark', () => {
    expect(html).toMatch(/class="[^"]*\bpl-ping\b[^"]*\bpl-ping--pulse\b/);
    expect(html).toMatch(/class="[^"]*\bpl-ping--on-ink\b/);
    expect(html).toMatch(/pl-ping__core/);
  });

  it('renders both an active and an idle face for the popup to toggle', () => {
    expect(html).toMatch(/data-when="active"/);
    expect(html).toMatch(/data-when="idle"/);
  });

  it('exposes the allowlist + join-code hooks popup.ts paints', () => {
    expect(html).toMatch(/data-allowlist\b/);
    expect(html).toMatch(/data-joincode\b/);
  });
});
