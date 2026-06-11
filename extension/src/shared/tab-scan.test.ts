import { describe, it, expect } from 'vitest';
import { selectTabsToBlock, type ScannableTab } from './tab-scan';
import type { AllowedDomain } from './host-matcher';

const EXT = 'chrome-extension://abcdefghijklmnop/';

const allow: AllowedDomain[] = [
  { matchType: 'Exact', value: 'outlook.office.com' },
  { matchType: 'Wildcard', value: '*.smartschool.be' },
];

const tab = (id: number | undefined, url?: string, pendingUrl?: string): ScannableTab => ({
  id,
  url,
  pendingUrl,
});

describe('selectTabsToBlock', () => {
  it('returns off-allowlist tabs with their id and url', () => {
    const result = selectTabsToBlock([tab(7, 'https://coolmathgames.com/play')], allow, EXT);
    expect(result).toEqual([{ tabId: 7, url: 'https://coolmathgames.com/play' }]);
  });

  it('leaves allowlisted tabs untouched', () => {
    const tabs = [
      tab(1, 'https://outlook.office.com/mail'),
      tab(2, 'https://app.smartschool.be/'),
    ];
    expect(selectTabsToBlock(tabs, allow, EXT)).toEqual([]);
  });

  it('blocks only the off-list tabs in a mixed set', () => {
    const tabs = [
      tab(1, 'https://outlook.office.com/mail'), // allowed
      tab(2, 'https://coolmathgames.com/'), // blocked
      tab(3, 'https://app.smartschool.be/'), // allowed
      tab(4, 'https://reddit.com/'), // blocked
    ];
    expect(selectTabsToBlock(tabs, allow, EXT)).toEqual([
      { tabId: 2, url: 'https://coolmathgames.com/' },
      { tabId: 4, url: 'https://reddit.com/' },
    ]);
  });

  it('skips the extension block page so it never redirects onto itself', () => {
    const tabs = [tab(5, `${EXT}block-page.html?blocked=https%3A%2F%2Freddit.com`)];
    expect(selectTabsToBlock(tabs, allow, EXT)).toEqual([]);
  });

  it('skips tabs without a usable id', () => {
    const tabs = [tab(undefined, 'https://reddit.com/'), tab(-1, 'https://reddit.com/')];
    expect(selectTabsToBlock(tabs, allow, EXT)).toEqual([]);
  });

  it('skips tabs whose url cannot be read', () => {
    // No host permission / blank new tab → query returns no url.
    const tabs = [tab(9, undefined, undefined)];
    expect(selectTabsToBlock(tabs, allow, EXT)).toEqual([]);
  });

  it('falls back to pendingUrl when the committed url is empty', () => {
    // Tab mid-navigation: committed url still blank, heading off-list.
    const tabs = [tab(3, undefined, 'https://reddit.com/')];
    expect(selectTabsToBlock(tabs, allow, EXT)).toEqual([
      { tabId: 3, url: 'https://reddit.com/' },
    ]);
  });

  it('prefers the committed url over pendingUrl when both are present', () => {
    const tabs = [tab(3, 'https://outlook.office.com/mail', 'https://reddit.com/')];
    // Committed url is allowed → not blocked, even though pendingUrl is off-list.
    expect(selectTabsToBlock(tabs, allow, EXT)).toEqual([]);
  });

  it('leaves non-http(s) browser pages alone', () => {
    const tabs = [
      tab(1, 'edge://settings'),
      tab(2, 'about:blank'),
      tab(3, 'file:///C:/Users/student/notes.txt'),
    ];
    expect(selectTabsToBlock(tabs, allow, EXT)).toEqual([]);
  });

  it('blocks every off-list tab when the allowlist is empty', () => {
    const tabs = [tab(1, 'https://outlook.office.com/'), tab(2, 'https://reddit.com/')];
    expect(selectTabsToBlock(tabs, [], EXT)).toEqual([
      { tabId: 1, url: 'https://outlook.office.com/' },
      { tabId: 2, url: 'https://reddit.com/' },
    ]);
  });
});
