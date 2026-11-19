// The extension's own page in Edge (edge://extensions/?id=…), driven the way a
// student uses it (#524). Its controls are Edge's own, so a spec that uses
// these skips on another channel: Chrome's page differs.

import { expect, type Page } from '@playwright/test';
import type { LoadedExtension } from './extension.ts';

/** Open the extension's own page in a new tab, once its controls are there. */
export async function openExtensionSettings(ext: LoadedExtension): Promise<Page> {
  const page = await ext.context.newPage();
  // Edge opens chrome:// pages as edge:// ones.
  await page.goto(`chrome://extensions/?id=${ext.extensionId}`);
  await expect(page.getByRole('combobox')).toHaveText(/On all sites|On click|On specific sites/);
  return page;
}

/** The "Site access" dropdown on the extension's page. */
export async function setSiteAccess(page: Page, value: 'ON_CLICK' | 'ON_ALL_SITES'): Promise<void> {
  await page.getByRole('combobox').click();
  await page.locator(`fluent-option[value="${value}"]`).click();
}

/**
 * The "Allow in InPrivate" switch on the extension's page. Changing it restarts
 * the extension, which reconnects to the agent.
 */
export async function allowInPrivate(page: Page): Promise<void> {
  await page.locator('fluent-switch:right-of(:text-is("Allow in InPrivate"))').first().click();
}
