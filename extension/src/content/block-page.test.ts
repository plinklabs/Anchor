import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

// AF4 (#165): the block page is a student-facing surface, so it is pinned to the
// DS **ink** treatment for its whole life — fixed, never following the OS
// light/dark setting (ANCHOR_BRAND.md §6). These regression tests fail if the
// ink pin is dropped or a system-following swap is reintroduced.
const html = readFileSync(
  fileURLToPath(new URL('./block-page.html', import.meta.url)),
  'utf8',
);

describe('block page — fixed ink treatment (AF4 / #165)', () => {
  it('hard-wears the DS ink treatment on <body>', () => {
    // `.plink-ink` is a class on the element, not a media query — that is what
    // makes the surface fixed ink on every machine.
    expect(html).toMatch(/<body[^>]*class="[^"]*\bplink-ink\b[^"]*"/);
  });

  it('pins color-scheme to dark so native controls/scrollbars stay ink', () => {
    expect(html).toMatch(/color-scheme:\s*dark/);
  });

  it('never follows the OS theme (no prefers-color-scheme media swap)', () => {
    // Guard the real swap — an @media (prefers-color-scheme: …) block — not a
    // mere textual mention (the source comment explains why we don't use one).
    expect(html).not.toMatch(/@media[^{]*prefers-color-scheme/);
  });
});

describe('block page — calm student-facing redesign (AE1 / #177)', () => {
  it('wears the signature concentric-ring ping as the focus-session mark', () => {
    // AE1 swaps the old static `pl-eyebrow__dot` for the living `pl-ping`.
    // It must be the pulsing, on-ink variant and carry all three layers
    // (two rings + core) the DS animation needs.
    expect(html).toMatch(/class="[^"]*\bpl-ping\b[^"]*\bpl-ping--pulse\b/);
    expect(html).toMatch(/class="[^"]*\bpl-ping--on-ink\b/);
    expect(html).toMatch(/pl-ping__ring[^"]*"[^>]*><\/span>\s*<span class="pl-ping__ring b/);
    expect(html).toMatch(/pl-ping__core/);
  });

  it('drops the old static eyebrow dot', () => {
    // The dot and the ping are mutually exclusive marks; leaving the dot in
    // would render two bullets before the label.
    expect(html).not.toMatch(/pl-eyebrow__dot/);
  });

  it('keeps the FOCUS SESSION eyebrow label', () => {
    // The eyebrow text the DS uppercases to "FOCUS SESSION".
    expect(html).toMatch(/pl-eyebrow[^>]*>[\s\S]*Focus session/i);
  });

  it('uses a calm, reassuring headline rather than a punitive one', () => {
    expect(html).toMatch(/<h1[^>]*>Let's stay on track<\/h1>/);
  });
});

describe('block page — the blocked URL stays legible (#318)', () => {
  // Pull the `.blocked-url` rule body out of the inline <style> so we assert on
  // its own declarations, not an incidental match elsewhere in the document.
  const blockedUrlRule = /\.blocked-url\s*\{([\s\S]*?)\}/.exec(html)?.[1] ?? '';

  it('declares the rule', () => {
    expect(blockedUrlRule).not.toBe('');
  });

  it('pins an explicit foreground colour instead of inheriting it', () => {
    // The white-on-white bug was inheritance: with no `color`, the URL text
    // colour depended entirely on the ink treatment resolving. Require an
    // explicit `color` declaration on the element itself.
    expect(blockedUrlRule).toMatch(/(^|[;{]|\s)color\s*:/);
  });

  it('gives both the foreground and inset background a literal fallback', () => {
    // The fallbacks keep the URL legible even if plink.css / its DS tokens
    // never resolve on the deployed page — a dark inset with light text rather
    // than a collapse to white-on-white. The pair must contrast.
    expect(blockedUrlRule).toMatch(/color\s*:\s*var\(--text,\s*#[0-9a-fA-F]{3,8}\s*\)/);
    expect(blockedUrlRule).toMatch(
      /background\s*:\s*var\(--surface-inset,\s*#[0-9a-fA-F]{3,8}\s*\)/,
    );
  });

  it('re-derives --surface-inset in the ink scope so the inset tracks the ink palette', () => {
    // Root cause: plink.css computes `--surface-inset: var(--paper-3)` once at
    // :root off the light paper; `.plink-ink` remaps `--paper-3` to dark but
    // never re-derives `--surface-inset`, so the inset stays light on this
    // fixed-ink page. `.block-root` must re-derive it from the remapped paper,
    // or the URL panel paints light and the on-ink text is invisible on it.
    const blockRootRule = /\.block-root\s*\{([\s\S]*?)\}/.exec(html)?.[1] ?? '';
    expect(blockRootRule).toMatch(/--surface-inset\s*:\s*var\(--paper-3/);
  });
});
