import { describe, it, expect, afterEach } from 'vitest';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { t } from './i18n';

// i18n (#322). Three layers of guard, none needing a real browser:
//   1. the t() lookup + fallback contract (the seam every surface calls);
//   2. catalogue health — en and nl carry the same keys (so nl is never missing
//      a translation) and the same placeholders;
//   3. the HTML ↔ catalogue mirror — every `data-i18n` key the pages reference
//      exists in both catalogues, and the inline English source copy matches the
//      `en` message so the two can't drift (the same mirror-lock idea as the
//      manifest/package.json version lock in manifest.test.ts).
//
// Per-key locale fallback (a key absent from nl rendering its English message)
// is Chrome's own `default_locale` behaviour; layer 2 keeps nl complete so it
// never has to fire in production, and layer 1 covers t()'s defensive fallback.

interface Message {
  message: string;
  placeholders?: Record<string, { content: string }>;
}
type Catalogue = Record<string, Message>;

const readJson = (rel: string): Catalogue =>
  JSON.parse(readFileSync(fileURLToPath(new URL(rel, import.meta.url)), 'utf8'));
const readText = (rel: string): string =>
  readFileSync(fileURLToPath(new URL(rel, import.meta.url)), 'utf8');

const en = readJson('../_locales/en/messages.json');
const nl = readJson('../_locales/nl/messages.json');

/** Pull every `data-i18n="key"` element and its inline text out of a page. The
 *  i18n elements hold plain text (no nested tags), so a single regex suffices. */
function i18nElements(html: string): Array<{ key: string; text: string }> {
  const re = /<[a-zA-Z0-9]+\b[^>]*\bdata-i18n="([^"]+)"[^>]*>([^<]*)</g;
  const out: Array<{ key: string; text: string }> = [];
  for (let m = re.exec(html); m; m = re.exec(html)) {
    out.push({ key: m[1], text: m[2] });
  }
  return out;
}

const normalize = (s: string) => s.replace(/\s+/g, ' ').trim();

describe('t() — lookup + fallback', () => {
  afterEach(() => {
    delete (globalThis as { chrome?: unknown }).chrome;
  });

  function stubGetMessage(impl: (key: string, subs?: string | string[]) => string) {
    (globalThis as { chrome?: unknown }).chrome = { i18n: { getMessage: impl } };
  }

  it('returns the catalogue message for a known key', () => {
    stubGetMessage((key) => (key === 'goBack' ? 'Go back' : ''));
    expect(t('goBack')).toBe('Go back');
  });

  it('forwards substitutions to chrome.i18n.getMessage', () => {
    const calls: Array<[string, string | string[] | undefined]> = [];
    stubGetMessage((key, subs) => {
      calls.push([key, subs]);
      return `Couldn't reach teacher: ${String(subs)}`;
    });
    expect(t('blockReachTeacherFailed', 'hub down')).toBe("Couldn't reach teacher: hub down");
    expect(calls).toEqual([['blockReachTeacherFailed', 'hub down']]);
  });

  it('falls back to the key when the message is missing from every catalogue', () => {
    // chrome.i18n.getMessage returns '' for an unknown key (even after the en
    // default_locale fallback) — t() must not surface a blank.
    stubGetMessage(() => '');
    expect(t('nope.not.here')).toBe('nope.not.here');
  });

  it('falls back to the key outside an extension context (no chrome.i18n)', () => {
    expect(t('goBack')).toBe('goBack');
  });
});

describe('catalogues — en is the source, nl mirrors it', () => {
  it('nl declares exactly the same keys as en', () => {
    expect(Object.keys(nl).sort()).toEqual(Object.keys(en).sort());
  });

  it('every message is a non-empty string in both locales', () => {
    for (const [key, msg] of [...Object.entries(en), ...Object.entries(nl)]) {
      expect(msg.message, key).toBeTypeOf('string');
      expect(msg.message.length, key).toBeGreaterThan(0);
    }
  });

  it('placeholders are declared identically in both locales', () => {
    for (const key of Object.keys(en)) {
      expect(Object.keys(en[key].placeholders ?? {}).sort(), key).toEqual(
        Object.keys(nl[key].placeholders ?? {}).sort(),
      );
    }
  });
});

describe('HTML ↔ catalogue mirror', () => {
  const pages = {
    'block-page.html': readText('../content/block-page.html'),
    'popup.html': readText('../content/popup.html'),
  };

  for (const [name, html] of Object.entries(pages)) {
    const elements = i18nElements(html);

    it(`${name} references at least one localized string`, () => {
      expect(elements.length).toBeGreaterThan(0);
    });

    for (const { key, text } of elements) {
      it(`${name}: "${key}" exists in both catalogues and matches the en source`, () => {
        expect(en[key], `en missing key ${key}`).toBeDefined();
        expect(nl[key], `nl missing key ${key}`).toBeDefined();
        // Inline English copy must equal the en catalogue message, so the
        // pre-script fallback text can never drift from what t() resolves.
        expect(normalize(text)).toBe(normalize(en[key].message));
      });
    }
  }
});
