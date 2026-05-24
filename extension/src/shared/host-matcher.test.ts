import { describe, it, expect } from 'vitest';
import { isUrlAllowed, type AllowedDomain } from './host-matcher';

const exact = (value: string): AllowedDomain => ({ matchType: 'Exact', value });
const wildcard = (value: string): AllowedDomain => ({ matchType: 'Wildcard', value });
const suffix = (value: string): AllowedDomain => ({ matchType: 'Suffix', value });

describe('isUrlAllowed', () => {
  describe('Exact', () => {
    it('matches the exact host', () => {
      expect(isUrlAllowed('https://outlook.office.com/mail', [exact('outlook.office.com')])).toBe(true);
    });

    it('does not match subdomains', () => {
      expect(isUrlAllowed('https://app.outlook.office.com/', [exact('outlook.office.com')])).toBe(false);
    });

    it('does not match unrelated hosts', () => {
      expect(isUrlAllowed('https://reddit.com/', [exact('outlook.office.com')])).toBe(false);
    });

    it('is case-insensitive on hostname', () => {
      expect(isUrlAllowed('https://Outlook.Office.COM/', [exact('outlook.office.com')])).toBe(true);
      expect(isUrlAllowed('https://outlook.office.com/', [exact('OUTLOOK.OFFICE.COM')])).toBe(true);
    });
  });

  describe('Wildcard *.host', () => {
    it('matches the bare suffix', () => {
      expect(isUrlAllowed('https://smartschool.be/', [wildcard('*.smartschool.be')])).toBe(true);
    });

    it('matches single-label subdomains', () => {
      expect(isUrlAllowed('https://app.smartschool.be/', [wildcard('*.smartschool.be')])).toBe(true);
    });

    it('matches deeper subdomains', () => {
      expect(isUrlAllowed('https://a.b.c.smartschool.be/', [wildcard('*.smartschool.be')])).toBe(true);
    });

    it('does not match hosts that merely end with the same string', () => {
      // `evilsmartschool.be` ends with `smartschool.be` but is not a subdomain.
      expect(isUrlAllowed('https://evilsmartschool.be/', [wildcard('*.smartschool.be')])).toBe(false);
    });

    it('does not match unrelated hosts', () => {
      expect(isUrlAllowed('https://reddit.com/', [wildcard('*.smartschool.be')])).toBe(false);
    });

    it('accepts a bare `foo.com` rule (no leading `*.`)', () => {
      // Catalogue authors don't always write the leading `*.`; treat both
      // forms the same so a missing star can't silently change semantics.
      expect(isUrlAllowed('https://x.smartschool.be/', [wildcard('smartschool.be')])).toBe(true);
      expect(isUrlAllowed('https://smartschool.be/', [wildcard('smartschool.be')])).toBe(true);
    });
  });

  describe('Suffix', () => {
    it('behaves like Wildcard for `*.foo` form', () => {
      expect(isUrlAllowed('https://app.smartschool.be/', [suffix('*.smartschool.be')])).toBe(true);
      expect(isUrlAllowed('https://evilsmartschool.be/', [suffix('*.smartschool.be')])).toBe(false);
    });

    it('behaves like Wildcard for bare-suffix form', () => {
      expect(isUrlAllowed('https://app.smartschool.be/', [suffix('smartschool.be')])).toBe(true);
    });
  });

  describe('non-http(s) URLs', () => {
    it('allows chrome-extension://', () => {
      expect(isUrlAllowed('chrome-extension://abc/block-page.html', [])).toBe(true);
    });

    it('allows about:blank', () => {
      expect(isUrlAllowed('about:blank', [])).toBe(true);
    });

    it('allows file:// URLs', () => {
      expect(isUrlAllowed('file:///C:/Users/student/notes.txt', [])).toBe(true);
    });

    it('allows edge:// internal pages', () => {
      expect(isUrlAllowed('edge://settings', [])).toBe(true);
    });

    it('allows javascript: pseudo-URLs', () => {
      expect(isUrlAllowed('javascript:void(0)', [])).toBe(true);
    });

    it('allows unparseable URLs (treats as not-a-navigation)', () => {
      expect(isUrlAllowed('not a url', [])).toBe(true);
    });
  });

  describe('multi-rule lists', () => {
    it('returns true if any rule matches', () => {
      const rules = [exact('outlook.office.com'), wildcard('*.smartschool.be')];
      expect(isUrlAllowed('https://app.smartschool.be/', rules)).toBe(true);
      expect(isUrlAllowed('https://outlook.office.com/', rules)).toBe(true);
      expect(isUrlAllowed('https://reddit.com/', rules)).toBe(false);
    });

    it('returns false on an empty rule list', () => {
      expect(isUrlAllowed('https://reddit.com/', [])).toBe(false);
    });
  });

  describe('malformed rules', () => {
    it('ignores empty values', () => {
      expect(isUrlAllowed('https://reddit.com/', [exact('')])).toBe(false);
      expect(isUrlAllowed('https://reddit.com/', [wildcard('*.')])).toBe(false);
    });

    it('ignores rules with an unknown match type', () => {
      const bogus = { matchType: 'NotARealType' as unknown as 'Exact', value: 'reddit.com' };
      expect(isUrlAllowed('https://reddit.com/', [bogus])).toBe(false);
    });
  });
});
