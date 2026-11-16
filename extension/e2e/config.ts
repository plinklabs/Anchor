// Shared constants for the Edge-extension end-to-end harness (#124).
//
// The harness drives the *real* extension in Edge, connected through the *real*
// native-messaging host to a stand-in for the on-box agent (fake-agent.ts): the
// agent is the extension's only source of the session (#489), so the specs hand
// sessions down the way the agent does, and read back what the extension
// reports. These values pin the loopback hosts the specs navigate to and the
// allowlists they hand down.

import path from 'node:path';
import { fileURLToPath } from 'node:url';
import type { Domain } from './fake-agent.ts';

const here = path.dirname(fileURLToPath(import.meta.url));

/** `extension/` — the project root for the unpacked extension + harness. */
export const EXTENSION_ROOT = path.resolve(here, '..');
/** The built, unpacked extension Edge loads. `npm run build` populates it. */
export const DIST_PATH = path.join(EXTENSION_ROOT, 'dist');
/** Repository root (one level above `extension/`). */
export const REPO_ROOT = path.resolve(EXTENSION_ROOT, '..');

// --- Stable extension identity --------------------------------------------
/**
 * The pinned extension id Edge derives from the manifest `key` (#123). Stable
 * across every machine so the school can force-install Anchor by id via Edge
 * policy. Documented canonically in extension/README.md ("Stable extension ID")
 * and locked by src/manifest.test.ts (manifest key → id). If you regenerate the
 * signing key, update all three together.
 */
export const STABLE_EXTENSION_ID = 'dnkimhodjfogjibnbbfdjdapgmmiojio';

// --- Loopback test hosts --------------------------------------------------
// All of them resolve to the harness's local static server (see static-server.ts)
// via Edge's --host-resolver-rules, so the specs never touch the public
// internet. What differs is whether the *hostname* lands on the allowlist the
// spec hands down:
//
//   BASELINE_ONLIST_HOST  127.0.0.1 — on every allowlist below (BASELINE).
//   BUNDLE_ONLY_HOST       covered by the Microsoft 365 bundle's
//                          `*.sharepoint.com` wildcard only, so it is on-list
//                          only while that bundle is in the allowlist — the
//                          lever the #93 amend spec pulls to make an open tab
//                          go off-list mid-session.
//   OFFLIST_HOST           never on any allowlist → always blocked once a
//                          session is active.
//   DASHBOARD_HOST         the dashboard's host, which the backend puts on
//                          every session's allowlist (#409).
export const BASELINE_ONLIST_HOST = '127.0.0.1';
export const BUNDLE_ONLY_HOST = 'team.sharepoint.com';
export const OFFLIST_HOST = 'offlist.test';
export const DASHBOARD_HOST = 'dashboard.test';
/** A host a spec grants mid-session, the way an approved unblock request does. */
export const GRANTED_HOST = 'granted.test';

/** Hosts Edge must resolve to the local static server (host → 127.0.0.1). */
export const MAPPED_HOSTS = [OFFLIST_HOST, BUNDLE_ONLY_HOST, DASHBOARD_HOST, GRANTED_HOST];

// --- Allowlists the specs hand down ----------------------------------------
// Shaped as the backend expands them (SessionAllowlist): the baseline the
// backend always adds, plus whatever bundles the teacher attached.

/** What every session allows: the dev baseline and the dashboard (#409). */
export const BASELINE: Domain[] = [
  { matchType: 'Exact', value: BASELINE_ONLIST_HOST },
  { matchType: 'Exact', value: DASHBOARD_HOST },
];

/** The baseline plus the seeded Microsoft 365 bundle's SharePoint entry. */
export const WITH_MS365: Domain[] = [...BASELINE, { matchType: 'Wildcard', value: '*.sharepoint.com' }];

// --- Browser launch knobs -------------------------------------------------
/** Edge by default; override (e.g. `chromium`) with E2E_BROWSER_CHANNEL. */
export const BROWSER_CHANNEL = process.env.E2E_BROWSER_CHANNEL ?? 'msedge';
/**
 * Headed by default — loading an MV3 extension needs a headed (or new-headless)
 * browser. Set E2E_HEADLESS=1 to opt into headless where the channel supports
 * extension loading.
 */
export const HEADLESS = process.env.E2E_HEADLESS === '1';
/**
 * The browser UI language every spec runs in unless it asks for another (#364).
 * The extension renders in the browser UI language (chrome.i18n, #322), and Edge
 * otherwise takes that from the host's Windows display language — so a spec that
 * asserts English copy passed on the en-US CI runner and failed on a Dutch dev
 * box. Pinned to what the CI runner has, so every machine renders the same copy.
 * A spec that needs another language passes `loadExtension({ locale })`.
 */
export const DEFAULT_LOCALE = 'en-US';
