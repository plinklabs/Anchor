import { defineConfig } from '@playwright/test';
import { HEADLESS } from './e2e/config.ts';

// Loading an MV3 extension requires a headed (or new-headless) browser, so
// workers run serially headed by default. No backend: the extension learns its
// session only from the on-box agent (#489), which each spec stands in for with
// a fake agent behind the real native-messaging host (e2e/fake-agent.ts).
export default defineConfig({
  testDir: './e2e/specs',
  // Extension service workers + the per-user native-host registration make
  // cross-test isolation easier with one worker; keep it simple and
  // deterministic.
  workers: 1,
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  timeout: 60_000,
  expect: { timeout: 15_000 },
  use: {
    headless: HEADLESS,
    trace: 'retain-on-failure',
  },
});
