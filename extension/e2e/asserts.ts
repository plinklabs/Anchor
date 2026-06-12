import { expect, type Page } from '@playwright/test';
import type { LoadedExtension } from './extension.ts';

/**
 * Assert the tab ended up on the extension's block page for `blockedHost`.
 *
 * Polls the URL *string* rather than using page.waitForURL: the block happens
 * by the service worker calling chrome.tabs.update, which aborts whatever
 * navigation was in flight (net::ERR_ABORTED). waitForURL watches the
 * navigation lifecycle and can surface that abort as an error, racing the
 * redirect; page.url() is a plain getter that never throws, so polling it is
 * immune to the abort.
 */
export async function expectRedirectedToBlockPage(
  page: Page,
  ext: LoadedExtension,
  blockedHost: string,
): Promise<void> {
  await expect.poll(() => page.url(), { timeout: 15_000 }).toContain('block-page.html');
  expect(page.url().startsWith(ext.blockPagePrefix)).toBe(true);
  await expect(page.locator('[data-blocked-url]')).toContainText(blockedHost);
}
