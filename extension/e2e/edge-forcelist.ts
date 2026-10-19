// Lets the e2e suite run on a developer box whose Edge policy force-installs
// Anchor (#429).
//
// Since #405 the installed agent adds Anchor's id to
// HKLM\SOFTWARE\Policies\Microsoft\Edge\ExtensionInstallForcelist (IT may put it
// under HKCU, through GPO or Intune). With that entry in place Edge refuses any
// unpacked copy that claims the same id, logging "Your organization blocks
// Anchor". dist/ claims it, because the manifest `key` pins the id (#123). The
// launch then never completes, and every spec hung for 60 s in the `ext` fixture
// with a timeout that didn't say why. CI never saw it: the runner has no agent.
//
// The harness can't change the policy: it needs elevation, and it belongs to the
// developer's agent. Instead it loads a copy of dist/ with the `key` removed.
// Edge then derives the id from the copy's path, which no forcelist names. Only
// the stable-id assertion can't hold that way, so extension-loads.spec.ts skips
// it on such a box.
//
// Edge policy is Windows-only, so detection is a no-op on every other platform.

import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';

/** The forcelist keys Edge honours, as reg.exe names them. HKLM first: it is
 *  where the agent writes its entry (#405). */
export const FORCELIST_KEYS = [
  'HKLM\\SOFTWARE\\Policies\\Microsoft\\Edge\\ExtensionInstallForcelist',
  'HKCU\\SOFTWARE\\Policies\\Microsoft\\Edge\\ExtensionInstallForcelist',
] as const;

/** One value of a forcelist key. */
export interface ForcelistEntry {
  /** The key it sits under, e.g. FORCELIST_KEYS[0]. */
  readonly key: string;
  /** The value name: the slot number Edge orders entries by. */
  readonly name: string;
  /** The entry itself: `<id>` or `<id>;<update url>`. */
  readonly data: string;
}

/** Describe an entry for a log line or skip reason. */
export function describeEntry(entry: ForcelistEntry): string {
  return `${entry.key} "${entry.name}" = ${entry.data}`;
}

/**
 * The entries in one key's `reg query` output that force-install `extensionId`.
 * Matches the way the agent recognises its own entry (EdgeExtensionPolicy.
 * IsAnchorEntry): the id segment before the first `;`, trimmed, any case.
 */
export function anchorEntriesIn(
  key: string,
  regQueryOutput: string,
  extensionId: string,
): ForcelistEntry[] {
  const entries: ForcelistEntry[] = [];
  // reg query prints each value as "    <name>    REG_SZ    <data>".
  for (const line of regQueryOutput.split(/\r?\n/)) {
    const match = /^ {4}(.+?) {4}REG_\w+ {4}(.*)$/.exec(line);
    if (!match) continue;
    const [, name, data] = match;
    const id = data.split(';')[0].trim();
    if (id.toLowerCase() === extensionId.toLowerCase()) {
      entries.push({ key, name, data: data.trim() });
    }
  }
  return entries;
}

/** `reg query <key>` in the native (64-bit) view Edge reads, or null when the
 *  key doesn't exist. */
function queryRegistryKey(key: string): string | null {
  try {
    return execFileSync('reg', ['query', key, '/reg:64'], { stdio: 'pipe' }).toString();
  } catch {
    return null;
  }
}

/**
 * The first forcelist entry, HKLM before HKCU, that makes Edge force-install
 * `extensionId` on this machine, or null when there is none (a CI runner, or a
 * box without the agent). Off Windows it returns null without querying anything.
 * `platform` and `query` exist for the unit tests.
 */
export function findForcelistEntry(
  extensionId: string,
  {
    platform = process.platform,
    query = queryRegistryKey,
  }: { platform?: NodeJS.Platform; query?: (key: string) => string | null } = {},
): ForcelistEntry | null {
  if (platform !== 'win32') return null;
  for (const key of FORCELIST_KEYS) {
    const output = query(key);
    if (output === null) continue;
    const [entry] = anchorEntriesIn(key, output, extensionId);
    if (entry) return entry;
  }
  return null;
}

/**
 * The id Edge gives an unpacked extension without a manifest `key`, loaded from
 * `dir` on Windows. Chromium's GenerateIdForPath: upper-case the drive letter,
 * hash the path's UTF-16LE bytes with SHA-256, keep the first 16 bytes and map
 * each hex digit 0-f to a-p. Checked against real Edge loads (see the unit test).
 * Pass the exact string Edge is launched with: Edge hashes it as given, without
 * resolving short (8.3) names or links.
 */
export function extensionIdForPath(dir: string): string {
  const normalized = /^[a-z]:/.test(dir) ? dir[0].toUpperCase() + dir.slice(1) : dir;
  return createHash('sha256')
    .update(Buffer.from(normalized, 'utf16le'))
    .digest()
    .subarray(0, 16)
    .toString('hex')
    .replace(/[0-9a-f]/g, (c) => String.fromCharCode('a'.charCodeAt(0) + parseInt(c, 16)));
}

/**
 * Copy the built extension at `distPath` to `dir`, replacing whatever is there,
 * and drop the manifest `key` from the copy so Edge derives its id from `dir`.
 */
export function stageKeylessCopy(distPath: string, dir: string): void {
  fs.rmSync(dir, { recursive: true, force: true });
  fs.cpSync(distPath, dir, { recursive: true });
  const manifestPath = path.join(dir, 'manifest.json');
  const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8')) as Record<string, unknown>;
  delete manifest.key;
  fs.writeFileSync(manifestPath, JSON.stringify(manifest, null, 2), 'utf8');
}
