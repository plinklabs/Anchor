import { defineConfig } from 'vitest/config';

// Vitest owns the pure-logic unit tests under src/ (host-matcher, session-state,
// tab-scan), the release packaging test under scripts/ (#210), and unit tests of
// e2e harness helpers that need no browser (e2e/*.test.ts, #429). The Playwright
// end-to-end specs live under e2e/specs/ and are run by `npm run e2e` — keep
// vitest from collecting them (they import @playwright/test and a browser-only
// `chrome`, which vitest can't run).
export default defineConfig({
  test: {
    include: ['src/**/*.test.ts', 'scripts/**/*.test.ts', 'e2e/*.test.ts'],
  },
});
