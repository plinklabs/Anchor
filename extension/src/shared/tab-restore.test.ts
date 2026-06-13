import { describe, it, expect } from 'vitest';
import { selectTabsToRestore, type RestorableTab } from './tab-restore';

const EXT = 'chrome-extension://abcdefghijklmnop/';
const BLOCK_PAGE = 'block-page.html';
const SESSION = 'session-123';

const tab = (id: number | undefined, url?: string): RestorableTab => ({ id, url });

/** Build a block-page URL the way redirectToBlockPage in background.ts does. */
const blockPage = (blocked: string | null, session: string | null): string => {
  const params = new URLSearchParams();
  if (blocked !== null) params.set('blocked', blocked);
  if (session !== null) params.set('session', session);
  return `${EXT}${BLOCK_PAGE}?${params.toString()}`;
};

describe('selectTabsToRestore', () => {
  it('restores a block-page tab to its original url for the ended session', () => {
    const tabs = [tab(7, blockPage('https://coolmathgames.com/play', SESSION))];
    expect(selectTabsToRestore(tabs, EXT, BLOCK_PAGE, SESSION)).toEqual([
      { tabId: 7, url: 'https://coolmathgames.com/play' },
    ]);
  });

  it('restores only the matching tabs in a mixed set', () => {
    const tabs = [
      tab(1, 'https://outlook.office.com/mail'), // not a block page
      tab(2, blockPage('https://reddit.com/', SESSION)), // restore
      tab(3, blockPage('https://coolmathgames.com/', SESSION)), // restore
    ];
    expect(selectTabsToRestore(tabs, EXT, BLOCK_PAGE, SESSION)).toEqual([
      { tabId: 2, url: 'https://reddit.com/' },
      { tabId: 3, url: 'https://coolmathgames.com/' },
    ]);
  });

  it('leaves non-Anchor tabs untouched', () => {
    const tabs = [
      tab(1, 'https://outlook.office.com/mail'),
      tab(2, 'https://app.smartschool.be/'),
    ];
    expect(selectTabsToRestore(tabs, EXT, BLOCK_PAGE, SESSION)).toEqual([]);
  });

  it('leaves block pages from a different (older) session untouched', () => {
    const tabs = [tab(2, blockPage('https://reddit.com/', 'session-OLD'))];
    expect(selectTabsToRestore(tabs, EXT, BLOCK_PAGE, SESSION)).toEqual([]);
  });

  it('leaves a block page with no session param untouched', () => {
    const tabs = [tab(2, blockPage('https://reddit.com/', null))];
    expect(selectTabsToRestore(tabs, EXT, BLOCK_PAGE, SESSION)).toEqual([]);
  });

  it('leaves the tab on the block page when blocked is missing', () => {
    const tabs = [tab(2, blockPage(null, SESSION))];
    expect(selectTabsToRestore(tabs, EXT, BLOCK_PAGE, SESSION)).toEqual([]);
  });

  it('leaves the tab on the block page when blocked is unparseable', () => {
    const tabs = [tab(2, blockPage('not a url', SESSION))];
    expect(selectTabsToRestore(tabs, EXT, BLOCK_PAGE, SESSION)).toEqual([]);
  });

  it('refuses to restore a non-http(s) blocked scheme', () => {
    const tabs = [tab(2, blockPage('javascript:alert(1)', SESSION))];
    expect(selectTabsToRestore(tabs, EXT, BLOCK_PAGE, SESSION)).toEqual([]);
  });

  it('skips tabs without a usable id', () => {
    const tabs = [
      tab(undefined, blockPage('https://reddit.com/', SESSION)),
      tab(-1, blockPage('https://reddit.com/', SESSION)),
    ];
    expect(selectTabsToRestore(tabs, EXT, BLOCK_PAGE, SESSION)).toEqual([]);
  });

  it('skips tabs with no url', () => {
    expect(selectTabsToRestore([tab(2, undefined)], EXT, BLOCK_PAGE, SESSION)).toEqual([]);
  });

  it('round-trips a blocked url that itself carries query params', () => {
    const original = 'https://example.com/watch?v=abc&t=30s';
    const tabs = [tab(9, blockPage(original, SESSION))];
    expect(selectTabsToRestore(tabs, EXT, BLOCK_PAGE, SESSION)).toEqual([
      { tabId: 9, url: original },
    ]);
  });
});
