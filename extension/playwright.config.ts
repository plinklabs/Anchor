import { defineConfig } from '@playwright/test';
import { BACKEND_PORT, HEADLESS } from './e2e/config.ts';

// Loading an MV3 extension requires a headed (or new-headless) browser, so
// workers run serially headed by default. The backend is booted once for the
// whole run via webServer (Playwright waits for the port to listen, which the
// backend opens only after seeding completes).
export default defineConfig({
  testDir: './e2e/specs',
  // Extension service workers + a single shared backend make cross-test
  // isolation easier with one worker; keep it simple and deterministic.
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
  webServer: {
    command: 'node e2e/run-backend.ts',
    port: BACKEND_PORT,
    // Always boot a fresh, dedicated backend in CI; locally reuse one already
    // listening on the e2e port for faster iteration.
    reuseExistingServer: !process.env.CI,
    timeout: 180_000,
    stdout: 'pipe',
    stderr: 'pipe',
  },
});
