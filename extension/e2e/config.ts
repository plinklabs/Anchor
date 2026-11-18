// Shared constants for the Edge-extension end-to-end harness (#124).
//
// The harness drives the *real* extension in Edge, connected through the *real*
// native-messaging host to a stand-in for the on-box agent (fake-agent.ts): the
// agent is the extension's only source of the session (#489), so the specs hand
// sessions down the way the agent does, and read back what the extension
// reports. These values pin the loopback hosts the specs navigate to and the
// allowlists they hand down.

import fs from 'node:fs';
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

/**
 * Hosts the Microsoft / school sign-in flow navigates through (#516), which
 * every session's baseline must keep reachable: Entra sign-in, its password
 * reset and its other sign-in hosts, the security-info registration sign-in
 * can send a student to, and personal-account sign-in.
 */
export const SIGN_IN_HOSTS = [
  'login.microsoftonline.com',
  'passwordreset.microsoftonline.com',
  'login.microsoft.com',
  'login.windows.net',
  'mysignins.microsoft.com',
  'login.live.com',
  'account.live.com',
];

/**
 * Microsoft's wider web, which the baseline used to open in every session
 * (#516) and which now comes only with the Microsoft 365 bundle.
 */
export const MICROSOFT_WEB_HOSTS = [
  'copilot.microsoft.com',
  'www.office.com',
  'outlook.office365.com',
  'outlook.live.com',
];

/**
 * Where Microsoft 365 on the web lives now (#531): its home page
 * m365.cloud.microsoft, where www.office.com redirects; www.microsoft365.com,
 * which redirects there too and which portal.office.com and sign-in send a
 * student through; and the apps on their *.cloud.microsoft hosts. Only the
 * Microsoft 365 bundle opens them. Every host under .microsoft is on Edge's HSTS
 * preload list, so the specs serve these over https from inside the browser
 * (static-server.ts, serveOverHttps).
 */
export const MICROSOFT_365_HOSTS = [
  'www.microsoft365.com',
  'm365.cloud.microsoft',
  'outlook.cloud.microsoft',
  'teams.cloud.microsoft',
  'word.cloud.microsoft',
  'forms.cloud.microsoft',
];

/**
 * The shortcuts a student types for Outlook and OneDrive (#532), each with the
 * host Microsoft redirects it on to. The extension judges a navigation by its
 * first URL, so the shortcut itself must be on the list, not only where it
 * leads; the Microsoft 365 bundle lists them as exact entries. Served over https
 * from inside the browser (static-server.ts, redirectOverHttps), the way a
 * student who types one reaches it; their targets come from the static server.
 */
export const MICROSOFT_365_SHORTCUTS = [
  { host: 'outlook.com', redirectsTo: 'outlook.live.com' },
  { host: 'www.outlook.com', redirectsTo: 'outlook.live.com' },
  { host: 'onedrive.com', redirectsTo: 'onedrive.live.com' },
  { host: 'www.onedrive.com', redirectsTo: 'onedrive.live.com' },
] as const;

/**
 * Google Fonts: a page's stylesheet and the font files it points at. Both load
 * as subresources, which the extension never filters, so no session lists them.
 */
export const WEB_FONT_CSS_HOST = 'fonts.googleapis.com';
export const WEB_FONT_FILE_HOST = 'fonts.gstatic.com';

/** Hosts Edge must resolve to the local static server (host → 127.0.0.1). */
export const MAPPED_HOSTS = [
  OFFLIST_HOST,
  BUNDLE_ONLY_HOST,
  DASHBOARD_HOST,
  GRANTED_HOST,
  ...SIGN_IN_HOSTS,
  ...MICROSOFT_WEB_HOSTS,
  // Served from inside the browser, never fetched; mapped all the same so a
  // request the spec didn't answer can't reach the real Microsoft 365.
  ...MICROSOFT_365_HOSTS,
  ...MICROSOFT_365_SHORTCUTS.map((s) => s.host),
  // Where the shortcuts lead (outlook.live.com is in MICROSOFT_WEB_HOSTS).
  'onedrive.live.com',
  WEB_FONT_CSS_HOST,
  WEB_FONT_FILE_HOST,
];

// --- Allowlists the specs hand down ----------------------------------------
// Shaped as the backend expands them (SessionAllowlist): the baseline the
// backend always adds, plus whatever bundles the teacher attached.

/**
 * The backend's baseline, the sign-in hosts (#516), read from the contract
 * Backend CI holds SessionAllowlist.BaselineDomains to — so the specs hand down
 * the list the backend really sends, not a copy that can drift from it.
 */
export const SIGN_IN_BASELINE: Domain[] = (
  JSON.parse(fs.readFileSync(path.join(REPO_ROOT, 'contracts', 'session-baseline.json'), 'utf8')) as {
    domains: Domain[];
  }
).domains;

/**
 * What every session allows: the backend's baseline, the dashboard (#409), and
 * the loopback host the specs use for an on-list page.
 */
export const BASELINE: Domain[] = [
  ...SIGN_IN_BASELINE,
  { matchType: 'Exact', value: BASELINE_ONLIST_HOST },
  { matchType: 'Exact', value: DASHBOARD_HOST },
];

/**
 * The baseline plus the Microsoft 365 bundle as DevDataSeeder and
 * scripts/seed-bundles.ps1 seed it. Its `*.microsoftonline.com` entry is left
 * out: the baseline has it already, and the backend lists it once.
 */
export const WITH_MS365: Domain[] = [
  ...BASELINE,
  { matchType: 'Wildcard', value: '*.office.com' },
  { matchType: 'Wildcard', value: '*.office365.com' },
  { matchType: 'Wildcard', value: '*.microsoft.com' },
  { matchType: 'Wildcard', value: '*.live.com' },
  { matchType: 'Wildcard', value: '*.sharepoint.com' },
  { matchType: 'Exact', value: 'outlook.office.com' },
  { matchType: 'Exact', value: 'teams.microsoft.com' },
  { matchType: 'Wildcard', value: '*.cloud.microsoft' },
  { matchType: 'Wildcard', value: '*.microsoft365.com' },
  { matchType: 'Exact', value: 'outlook.com' },
  { matchType: 'Exact', value: 'www.outlook.com' },
  { matchType: 'Exact', value: 'onedrive.com' },
  { matchType: 'Exact', value: 'www.onedrive.com' },
];

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
