import { defineConfig } from 'vitest/config';

// Vitest owns the pure-logic unit tests under src/ (host-matcher, session-state,
// tab-scan) and the release packaging test under scripts/ (#210). The Playwright
// end-to-end specs live under e2e/ and are run by `npm run e2e` — keep vitest
// from collecting them (they import @playwright/test and a browser-only `chrome`,
// which vitest can't run).
export default defineConfig({
  test: {
    include: ['src/**/*.test.ts', 'scripts/**/*.test.ts'],
  },
});
