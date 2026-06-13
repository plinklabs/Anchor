// Shared constants for the Edge-extension end-to-end harness (#124).
//
// The harness drives the *real* extension in Edge against a *real* backend,
// with session lifecycle pushed over REST + SignalR — no mocks, no stubbed
// hub. These values pin the seeded dev identities / class / bundle the backend
// creates (see backend/src/Anchor.Infrastructure/Persistence/DevDataSeeder.cs)
// and the loopback hosts the specs navigate to.

import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));

/** `extension/` — the project root for the unpacked extension + harness. */
export const EXTENSION_ROOT = path.resolve(here, '..');
/** The built, unpacked extension Edge loads. `npm run build` populates it. */
export const DIST_PATH = path.join(EXTENSION_ROOT, 'dist');
/** Repository root (one level above `extension/`). */
export const REPO_ROOT = path.resolve(EXTENSION_ROOT, '..');
/** The backend project the harness boots. */
export const BACKEND_PROJECT = path.join(REPO_ROOT, 'backend', 'src', 'Anchor.Api');

/**
 * Dedicated e2e backend port — deliberately *not* the dev default (5276) so a
 * running dev backend (and its real `anchor.dev.db`) is never reused or
 * polluted by a test run. Override with E2E_BACKEND_PORT.
 */
export const BACKEND_PORT = Number(process.env.E2E_BACKEND_PORT ?? 5281);
export const BACKEND_URL = `http://127.0.0.1:${BACKEND_PORT}`;

/**
 * Throwaway SQLite file for the e2e backend, under the OS temp dir so it never
 * lands in the repo. run-backend.ts deletes it (+ -wal/-shm) before each boot
 * so every run starts from a freshly-seeded schema — sidesteps the dev-DB
 * schema-drift footgun (EnsureCreatedAsync doesn't migrate).
 */
export const E2E_DB_PATH = path.join(os.tmpdir(), 'anchor-e2e.db');

// --- Stable extension identity --------------------------------------------
/**
 * The pinned extension id Edge derives from the manifest `key` (#123). Stable
 * across every machine so the school can force-install Anchor by id via Edge
 * policy. Documented canonically in extension/README.md ("Stable extension ID")
 * and locked by src/manifest.test.ts (manifest key → id). If you regenerate the
 * signing key, update all three together.
 */
export const STABLE_EXTENSION_ID = 'akkfdaclmpfcnjalcifkcbhgjnnopman';

// --- Seeded dev identities (mirror DevDataSeeder) -------------------------
export const TEACHER_OID = '11111111-1111-1111-1111-111111111111';
export const STUDENT_OID = '22222222-2222-2222-2222-222222222222';
export const CLASS_NAME = '3A';
/** Bundle whose entries include *.sharepoint.com (see BUNDLE_ONLY_HOST). */
export const MS365_BUNDLE_NAME = 'Microsoft 365';

// --- Loopback test hosts --------------------------------------------------
// All three resolve to the harness's local static server (see static-server.ts)
// via Edge's --host-resolver-rules, so the specs never touch the public
// internet. What differs is whether the *hostname* lands on the allowlist:
//
//   BASELINE_ONLIST_HOST  127.0.0.1 — always allowed in a Development build
//                          via the #125 dev carve-out (SessionAllowlist).
//   BUNDLE_ONLY_HOST       *.sharepoint.com is in the Microsoft 365 bundle but
//                          NOT the baseline, so it's on-list only while that
//                          bundle is attached — the lever the #93 amend spec
//                          pulls to make an open tab go off-list mid-session.
//   OFFLIST_HOST           never on any allowlist → always blocked once a
//                          session is active.
export const BASELINE_ONLIST_HOST = '127.0.0.1';
export const BUNDLE_ONLY_HOST = 'team.sharepoint.com';
export const OFFLIST_HOST = 'offlist.test';

/** Hosts Edge must resolve to the local static server (host → 127.0.0.1). */
export const MAPPED_HOSTS = [OFFLIST_HOST, BUNDLE_ONLY_HOST];

// --- Browser launch knobs -------------------------------------------------
/** Edge by default; override (e.g. `chromium`) with E2E_BROWSER_CHANNEL. */
export const BROWSER_CHANNEL = process.env.E2E_BROWSER_CHANNEL ?? 'msedge';
/**
 * Headed by default — loading an MV3 extension needs a headed (or new-headless)
 * browser. Set E2E_HEADLESS=1 to opt into headless where the channel supports
 * extension loading.
 */
export const HEADLESS = process.env.E2E_HEADLESS === '1';
