// i18n (#322, part of #320). The extension localizes through Chrome's native
// `chrome.i18n`: catalogues live in `_locales/<lang>/messages.json`, the active
// locale is the browser UI language, and `default_locale` (`en`, set in
// manifest.json) is the built-in fallback — a key missing from the active locale
// resolves to its English message automatically, never a blank or a raw key.
//
// This module is the thin seam the surfaces call so no page touches
// `chrome.i18n` directly:
//   • `t(key, subs?)`   — look a message up (with optional `$1`-style subs).
//   • `localizeDocument` — fill every `[data-i18n]` element's text from the
//     catalogue and stamp <html lang> to the UI language.
//
// English source copy is also kept inline in the HTML (so the page reads
// correctly before this runs and if scripting is ever disabled); it mirrors the
// `en` catalogue and i18n.test.ts locks the two together so they can't drift.

/** Substitutions for a message's `$1`…`$9` placeholders (see messages.json). */
export type MessageSubstitutions = string | string[];

/**
 * Resolve a catalogue message by key. Delegates to `chrome.i18n.getMessage`,
 * which already falls back to `default_locale` (en) for a key the active locale
 * is missing. The last-resort `?? key` only bites when `chrome.i18n` is absent
 * (e.g. a non-extension context) or the key is in *no* catalogue — far better
 * than rendering an empty string.
 */
export function t(key: string, substitutions?: MessageSubstitutions): string {
  const getMessage = globalThis.chrome?.i18n?.getMessage;
  const message = getMessage?.(key, substitutions);
  return message || key;
}

/**
 * Localize a rendered document in place: set each `[data-i18n]` element's text
 * to its catalogue message, and stamp `<html lang>` to the resolved UI language
 * (so assistive tech and the browser know what language the page is actually
 * showing). Idempotent — in the English locale it rewrites the same source copy.
 */
export function localizeDocument(root: Document = document): void {
  root.querySelectorAll<HTMLElement>('[data-i18n]').forEach((el) => {
    const key = el.dataset.i18n;
    if (key) el.textContent = t(key);
  });

  const uiLanguage = globalThis.chrome?.i18n?.getUILanguage?.();
  if (uiLanguage) root.documentElement.lang = uiLanguage;
}
