// Build the self-hosted test-ring extension (#497): a CRX3 signed with our own
// key, plus the update manifest Edge polls for it.
//
// The test ring is a second Intune profile that force-installs this build from
// our own update URL instead of the store's, so a change reaches a few test
// laptops without waiting days for the Edge Add-ons review. The Store build is
// untouched (pack-extension.mjs, extension-release.yml).
//
// This is the local-runnable core of .github/workflows/extension-test-ring.yml,
// as pack-extension.mjs is of the store release: the workflow only fetches the
// published update manifest, runs this, and uploads what it writes.
//
// Steps:
//   1. Read package.json `version` (#208) and cross-check a tag version against it.
//   2. `npm run build` → dist/.
//   3. Derive the public key and extension ID from the private key, and refuse a
//      key whose ID isn't the one extension/test-ring.json names: the agent only
//      takes that ID for Anchor's (AnchorExtensionIds), so a build signed with any
//      other key would install on the test laptops and never be let onto the
//      witness link.
//   4. Rewrite dist/manifest.json for the test ring: version
//      `<package version>.<build number>` (the build number makes every publish
//      newer than the last, which Edge requires before it updates), `update_url`
//      pointing at our update manifest (Edge follows it for every update after the
//      first install), and `key` set to the test-ring public key.
//   5. Zip, sign into artifacts/test-ring/anchor-extension-test-ring-<version>.crx,
//      and write artifacts/test-ring/updates.xml naming that CRX.
//   6. With the currently published updates.xml given, refuse a version older than
//      the published one: Edge would never install it, so the publish would look
//      green and change nothing.
//
// The private key comes from the environment (EXTENSION_TEST_RING_KEY, PEM), never
// a file in the repo, and nothing here prints it.
//
// Usage (CI passes all of these):
//   EXTENSION_TEST_RING_KEY="$(cat key.pem)" node scripts/pack-test-ring.mjs \
//     --repo plinklabs/Anchor --build-number 12 [--version 0.4.4] [--published updates.xml] [--no-build]

import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { extensionIdFromPublicKey, packCrx3, publicKeyDer } from './crx3.mjs';
import { isMainModule } from './is-main-module.mjs';
import { listFiles, readSourceVersion } from './pack-extension.mjs';
import { makeZip } from './zip.mjs';

const scriptDir = dirname(fileURLToPath(import.meta.url));
const extensionDir = resolve(scriptDir, '..');
const DIST_DIR = join(extensionDir, 'dist');
/** The one place the test-ring ID is configured (the agent embeds it too). */
export const TEST_RING_CONFIG_PATH = join(extensionDir, 'test-ring.json');
export const TEST_RING_OUT_DIR = join(extensionDir, 'artifacts', 'test-ring');

/** The GitHub release that hosts the test-ring build, next to the agent's feed. */
export const TEST_RING_TAG = 'extension-test-ring';
export const UPDATE_MANIFEST_NAME = 'updates.xml';
/** The repository secret holding the PEM private key. */
export const KEY_ENV = 'EXTENSION_TEST_RING_KEY';

/** `{ extensionId, publicKey }` from extension/test-ring.json; empty strings until the key exists. */
export function readTestRingConfig(path = TEST_RING_CONFIG_PATH) {
  const config = JSON.parse(readFileSync(path, 'utf8'));
  return { extensionId: config.extensionId ?? '', publicKey: config.publicKey ?? '' };
}

/** Where a repository (`owner/name`) hosts the test-ring update manifest and CRX. */
export function testRingUrls(repo) {
  if (!/^[\w.-]+\/[\w.-]+$/.test(repo ?? '')) {
    throw new Error(`"${repo}" is not a GitHub repository (owner/name).`);
  }
  const base = `https://github.com/${repo}/releases/download/${TEST_RING_TAG}`;
  return {
    updateUrl: `${base}/${UPDATE_MANIFEST_NAME}`,
    crxName: (version) => `anchor-extension-test-ring-${version}.crx`,
    crxUrl: (version) => `${base}/anchor-extension-test-ring-${version}.crx`,
  };
}

/**
 * `<package version>.<build number>`: Edge versions are one to four numbers of at
 * most 65535, and Edge only installs a version newer than the one it has.
 */
export function testRingVersion(sourceVersion, buildNumber) {
  if (!/^\d+\.\d+\.\d+$/.test(sourceVersion)) {
    throw new Error(`package.json version "${sourceVersion}" isn't major.minor.patch.`);
  }
  const build = Number(buildNumber);
  if (!Number.isInteger(build) || build < 0 || build > 65535 || String(buildNumber).trim() === '') {
    throw new Error(`Build number "${buildNumber}" must be a whole number from 0 to 65535.`);
  }
  return `${sourceVersion}.${build}`;
}

/** -1, 0 or 1 as Edge orders two extension versions. */
export function compareVersions(a, b) {
  const pa = a.split('.').map(Number);
  const pb = b.split('.').map(Number);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const d = (pa[i] ?? 0) - (pb[i] ?? 0);
    if (d !== 0) return Math.sign(d);
  }
  return 0;
}

const escapeXml = (s) =>
  s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/'/g, '&apos;');

/** The gupdate update manifest Edge polls: one app, one CRX, one version. */
export function updateManifest({ extensionId, crxUrl, version }) {
  return [
    "<?xml version='1.0' encoding='UTF-8'?>",
    "<gupdate xmlns='http://www.google.com/update2/response' protocol='2.0'>",
    `  <app appid='${escapeXml(extensionId)}'>`,
    `    <updatecheck codebase='${escapeXml(crxUrl)}' version='${escapeXml(version)}' />`,
    '  </app>',
    '</gupdate>',
    '',
  ].join('\n');
}

/** The version a published update manifest offers, or null when it names none. */
export function publishedVersion(xml) {
  return /<updatecheck\b[^>]*\bversion=['"]([^'"]+)['"]/.exec(xml)?.[1] ?? null;
}

/**
 * Build, sign and write the test-ring CRX and update manifest. Returns the paths
 * and what they name. Throws, before writing anything, on a version mismatch, a key
 * that doesn't match test-ring.json, or a version older than `publishedXml`'s.
 * `distDir` (with `build: false`) packages an existing build from elsewhere; the
 * tests use it so they never rebuild dist/ under another test that reads it.
 */
export function packTestRing({
  privateKeyPem,
  repo,
  buildNumber,
  version,
  publishedXml,
  build = true,
  distDir = DIST_DIR,
  configPath = TEST_RING_CONFIG_PATH,
  outDir = TEST_RING_OUT_DIR,
} = {}) {
  if (!privateKeyPem || !privateKeyPem.trim()) {
    throw new Error(`No test-ring signing key: set ${KEY_ENV} to the PEM private key.`);
  }
  const sourceVersion = readSourceVersion();
  if (version && version !== sourceVersion) {
    throw new Error(
      `Requested version "${version}" does not match the committed single version ` +
        `source "${sourceVersion}" in extension/package.json.`,
    );
  }
  const ringVersion = testRingVersion(sourceVersion, buildNumber);
  const urls = testRingUrls(repo);

  // Trimmed: a secret pasted with a leading blank line or space is still the key.
  const pem = privateKeyPem.trim();
  const publicKey = publicKeyDer(pem);
  const extensionId = extensionIdFromPublicKey(publicKey);
  const config = readTestRingConfig(configPath);
  if (config.extensionId !== extensionId) {
    throw new Error(
      config.extensionId
        ? `${KEY_ENV} signs as ${extensionId}, but extension/test-ring.json names ` +
            `${config.extensionId}, the only test-ring ID the agent accepts. Put the matching ` +
            'key in the secret (or, for a new key, see docs/RELEASE.md "Extension test ring").'
        : 'extension/test-ring.json names no test-ring ID yet, so no agent would accept this ' +
            `build. Run scripts/test-ring-key.mjs (or record ${extensionId} there), release ` +
            'the agent, then publish (docs/RELEASE.md "Extension test ring").',
    );
  }

  const published = publishedXml ? publishedVersion(publishedXml) : null;
  if (published && compareVersions(ringVersion, published) < 0) {
    throw new Error(
      `The test ring already has ${published}, newer than ${ringVersion}; Edge wouldn't ` +
        'install this build. Publish from a branch that has the current package.json version.',
    );
  }

  if (build) {
    execFileSync('npm', ['run', 'build'], { cwd: extensionDir, stdio: 'inherit', shell: true });
  }
  const manifestPath = join(distDir, 'manifest.json');
  if (!existsSync(manifestPath)) throw new Error('dist/ has no manifest.json; build first.');
  const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
  if (manifest.version !== sourceVersion) {
    throw new Error(
      `Built dist/manifest.json version "${manifest.version}" does not match ` +
        `"${sourceVersion}". Rebuild (drop --no-build).`,
    );
  }

  const ringManifest = {
    ...manifest,
    version: ringVersion,
    version_name: `${sourceVersion} test ring ${ringVersion.split('.').at(-1)}`,
    update_url: urls.updateUrl,
    key: publicKey.toString('base64'),
  };
  const entries = listFiles(distDir)
    .sort()
    .map((name) => ({
      name,
      data:
        name === 'manifest.json'
          ? Buffer.from(JSON.stringify(ringManifest, null, 2) + '\n')
          : readFileSync(join(distDir, name)),
    }));
  const crx = packCrx3(makeZip(entries), pem);

  rmSync(outDir, { recursive: true, force: true });
  mkdirSync(outDir, { recursive: true });
  const crxPath = join(outDir, urls.crxName(ringVersion));
  const updatesPath = join(outDir, UPDATE_MANIFEST_NAME);
  writeFileSync(crxPath, crx);
  writeFileSync(
    updatesPath,
    updateManifest({ extensionId, crxUrl: urls.crxUrl(ringVersion), version: ringVersion }),
  );

  return {
    crxPath,
    updatesPath,
    extensionId,
    version: ringVersion,
    updateUrl: urls.updateUrl,
    crxUrl: urls.crxUrl(ringVersion),
  };
}

/** Parse `--name value` / `--name=value` / `--no-build` from argv. */
function parseArgs(argv) {
  const opts = { build: true };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--no-build') {
      opts.build = false;
      continue;
    }
    const m = /^--(repo|build-number|version|published)(?:=(.*))?$/.exec(a);
    if (!m) throw new Error(`Unknown argument "${a}".`);
    opts[m[1]] = m[2] ?? argv[++i];
  }
  return opts;
}

/** Run the CLI the workflow calls; the key comes from the environment. */
function main(argv) {
  const opts = parseArgs(argv);
  const publishedPath = opts.published;
  const result = packTestRing({
    privateKeyPem: process.env[KEY_ENV],
    repo: opts.repo,
    buildNumber: opts['build-number'],
    version: opts.version || undefined,
    publishedXml: publishedPath && existsSync(publishedPath) ? readFileSync(publishedPath, 'utf8') : undefined,
    build: opts.build,
  });
  if (process.env.GITHUB_OUTPUT) {
    writeFileSync(
      process.env.GITHUB_OUTPUT,
      [
        `crx=${result.crxPath}`,
        `updates=${result.updatesPath}`,
        `version=${result.version}`,
        `id=${result.extensionId}`,
        `update_url=${result.updateUrl}`,
        '',
      ].join('\n'),
      { flag: 'a' },
    );
  }
  console.log(`== Test-ring build ${result.version} (${result.extensionId}) → ${result.crxPath} ==`);
  console.log(`   Forcelist entry: ${result.extensionId};${result.updateUrl}`);
}

if (isMainModule(import.meta.url, process.argv[1])) {
  try {
    main(process.argv.slice(2));
  } catch (e) {
    // An annotation in Actions, so the cause shows on the run's summary page.
    console.error(`${process.env.GITHUB_ACTIONS ? '::error::' : 'error: '}${e.message}`);
    process.exitCode = 1;
  }
}
