// Build the extension and package dist/ into a versioned ZIP for the Edge
// Add-ons store (#210).
//
// This is the local-runnable core of the tag-triggered release pipeline
// (.github/workflows/extension-release.yml) — the agent's pack-release.ps1
// equivalent for the extension. The workflow is a thin wrapper: it installs Node,
// `npm ci`, then calls this script. Running it locally produces the exact same
// artifact, which is the point — the build logic is reviewable/testable here, not
// buried in YAML.
//
// Steps:
//   1. Read the single version source (package.json `version`, #208).
//   2. If a tag version is supplied (--version / env PACK_VERSION), cross-check it
//      against package.json and fail loudly on drift — the same guard the agent's
//      pack script applies, so a tag can never ship a surprising number.
//   3. `npm run build` (rollup) → dist/, which stamps the version into the copied
//      manifest (rollup.config.mjs, #208). dist/ keeps the committed `key` so an
//      *unpacked* local-dev / e2e load installs as the stable ID the dev
//      native-messaging host's allowed_origins pins.
//   4. Zip dist/ into artifacts/anchor-extension-<version>.zip — the upload the
//      Edge Add-ons submission consumes — but STRIP `key` from the packaged
//      manifest. The Edge Add-ons store assigns the extension ID itself and
//      rejects any manifest that ships a `key` ("The manifest shouldn't contain
//      the key field"). The store-published ID is therefore store-assigned, not
//      the committed key's `akkfda…`; the agent-side pins (EdgeExtensionPolicy,
//      witness-host allowed_origins, force-install policy) must be updated to the
//      store-assigned ID once the product exists.
//
// Usage:
//   node scripts/pack-extension.mjs                      # version from package.json
//   node scripts/pack-extension.mjs --version 0.2.0      # cross-check against tag
//   node scripts/pack-extension.mjs --no-build           # package an existing dist/
//   PACK_VERSION=0.2.0 node scripts/pack-extension.mjs   # CI passes the tag version

import { execFileSync } from 'node:child_process';
import {
  mkdirSync,
  readFileSync,
  readdirSync,
  rmSync,
  statSync,
  writeFileSync,
} from 'node:fs';
import { dirname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { makeZip } from './zip.mjs';

const scriptDir = dirname(fileURLToPath(import.meta.url));
const extensionDir = resolve(scriptDir, '..');
const distDir = join(extensionDir, 'dist');
const artifactsDir = join(extensionDir, 'artifacts');
const packageJsonPath = join(extensionDir, 'package.json');

/** Parse `--version <v>` / `--version=<v>` / `--no-build` from argv. */
function parseArgs(argv) {
  const opts = { version: process.env.PACK_VERSION || undefined, build: true };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--no-build') opts.build = false;
    else if (a === '--version') opts.version = argv[++i];
    else if (a.startsWith('--version=')) opts.version = a.slice('--version='.length);
  }
  return opts;
}

/** package.json `version` is the single source of truth (#208). */
function readSourceVersion() {
  const pkg = JSON.parse(readFileSync(packageJsonPath, 'utf8'));
  if (!pkg.version) throw new Error('extension/package.json declares no "version".');
  return String(pkg.version);
}

/** Recursively list files under `dir`, returning archive-relative POSIX paths. */
function listFiles(dir, baseDir = dir) {
  const out = [];
  for (const name of readdirSync(dir)) {
    const full = join(dir, name);
    if (statSync(full).isDirectory()) out.push(...listFiles(full, baseDir));
    else out.push(relative(baseDir, full).split(sep).join('/'));
  }
  return out;
}

/**
 * Build + package the extension. Exported so the unit test can drive it without
 * spawning a subprocess. Returns the absolute path to the written .zip.
 */
export function packExtension({ version, build = true } = {}) {
  const sourceVersion = readSourceVersion();
  if (version && version !== sourceVersion) {
    throw new Error(
      `Requested version "${version}" does not match the committed single ` +
        `version source "${sourceVersion}" in extension/package.json. Bump ` +
        `"version" and tag to match (see extension/README.md).`,
    );
  }
  const releaseVersion = version || sourceVersion;

  if (build) {
    // Rollup emits dist/ and stamps releaseVersion into the copied manifest.
    // `npm.cmd` on Windows; `npm` elsewhere. shell:true lets the platform PATH
    // resolve the right one.
    execFileSync('npm', ['run', 'build'], {
      cwd: extensionDir,
      stdio: 'inherit',
      shell: true,
    });
  }

  // Guard: the built manifest must carry the release version. This is the same
  // invariant manifest.test.ts locks, re-checked at pack time so a stale dist/
  // (e.g. --no-build against an old build) can never ship the wrong number.
  const distManifest = JSON.parse(readFileSync(join(distDir, 'manifest.json'), 'utf8'));
  if (distManifest.version !== releaseVersion) {
    throw new Error(
      `Built dist/manifest.json version "${distManifest.version}" does not match ` +
        `the release version "${releaseVersion}". Rebuild (drop --no-build).`,
    );
  }
  const files = listFiles(distDir).sort();
  if (files.length === 0) throw new Error('dist/ is empty — nothing to package.');
  const entries = files.map((name) => {
    if (name === 'manifest.json') {
      // Strip `key` from the packaged manifest: the Edge Add-ons store assigns
      // the ID and rejects a manifest that ships a `key`. dist/manifest.json
      // keeps it for unpacked local dev (see header); only the store upload omits
      // it.
      const manifest = JSON.parse(readFileSync(join(distDir, name), 'utf8'));
      delete manifest.key;
      return { name, data: Buffer.from(JSON.stringify(manifest, null, 2) + '\n') };
    }
    return { name, data: readFileSync(join(distDir, name)) };
  });
  const zip = makeZip(entries);

  rmSync(artifactsDir, { recursive: true, force: true });
  mkdirSync(artifactsDir, { recursive: true });
  const zipPath = join(artifactsDir, `anchor-extension-${releaseVersion}.zip`);
  writeFileSync(zipPath, zip);
  return zipPath;
}

// Run only when invoked directly (`node scripts/pack-extension.mjs`), not when
// imported by the test.
if (process.argv[1] && resolve(process.argv[1]) === resolve(fileURLToPath(import.meta.url))) {
  const opts = parseArgs(process.argv.slice(2));
  const zipPath = packExtension(opts);
  // Emit the path on GITHUB_OUTPUT when running in Actions so the workflow can
  // reference the artifact without re-deriving the name.
  if (process.env.GITHUB_OUTPUT) {
    writeFileSync(process.env.GITHUB_OUTPUT, `zip=${zipPath}\n`, { flag: 'a' });
  }
  console.log(`== Packaged extension → ${zipPath} ==`);
}
