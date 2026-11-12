// Create the test ring's signing key (#497) and record its ID in the repo.
//
// The self-hosted test-ring build is signed with our own RSA key, and Edge derives
// the build's extension ID from that key, so making the key fixes the ID. This
// script makes a 2048-bit key pair on this machine and:
//
//   * writes the PRIVATE key to a PEM file OUTSIDE the repository (it refuses a
//     path inside it, and never overwrites an existing key file): that file goes
//     into the EXTENSION_TEST_RING_KEY repository secret and an offline store, and
//     nowhere else;
//   * writes only the public half and the derived ID into extension/test-ring.json,
//     the one place the ID is configured: the agent build embeds it
//     (AnchorExtensionIds) and the test-ring packaging checks the key against it.
//
// It prints the ID, the Intune forcelist entry and the next steps, and nothing
// secret. It sets no secret itself: that's a deliberate manual step
// (docs/RELEASE.md "Extension test ring").
//
// A new key means a new ID, which every test-ring laptop's policy names, so the
// script refuses to replace an ID that test-ring.json already has unless --force.
//
// Usage:
//   node scripts/test-ring-key.mjs [--out <pem path outside the repo>] [--repo owner/name] [--force]

import { execFileSync } from 'node:child_process';
import { createHash, generateKeyPairSync } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, isAbsolute, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { extensionIdFromPublicKey } from './crx3.mjs';
import { isMainModule } from './is-main-module.mjs';
import { KEY_ENV, TEST_RING_CONFIG_PATH, testRingUrls } from './pack-test-ring.mjs';

const scriptDir = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(scriptDir, '..', '..');

export const DEFAULT_KEY_PATH = join(homedir(), '.anchor', 'extension-test-ring.pem');

/** True when `path` is `root` or anywhere below it. */
function isInside(root, path) {
  const rel = relative(resolve(root), resolve(path));
  return rel === '' || (!rel.startsWith('..') && !isAbsolute(rel));
}

/**
 * Make the key pair, write the private key to `out` and the public key + ID to
 * `configPath`. Returns `{ extensionId, publicKey, keyPath }`. Throws, writing
 * nothing, when `out` is inside `root`, already exists, or `configPath` already
 * names an ID (unless `force`).
 */
export function createTestRingKey({
  out = DEFAULT_KEY_PATH,
  configPath = TEST_RING_CONFIG_PATH,
  root = repoRoot,
  force = false,
} = {}) {
  const keyPath = resolve(out);
  if (isInside(root, keyPath)) {
    throw new Error(
      `Refusing to write the private key inside the repository (${keyPath}). ` +
        'Pick a path outside it, e.g. --out ~/.anchor/extension-test-ring.pem.',
    );
  }
  if (existsSync(keyPath)) {
    throw new Error(`${keyPath} already exists; not overwriting a key. Move it away first, or pick another --out.`);
  }
  const config = JSON.parse(readFileSync(configPath, 'utf8'));
  if (config.extensionId && !force) {
    throw new Error(
      `extension/test-ring.json already names ${config.extensionId}. A new key means a new ID, ` +
        'and every test-ring laptop policy names the old one. Pass --force to replace it anyway.',
    );
  }

  const { publicKey, privateKey } = generateKeyPairSync('rsa', {
    modulusLength: 2048,
    publicKeyEncoding: { type: 'spki', format: 'der' },
    privateKeyEncoding: { type: 'pkcs8', format: 'pem' },
  });
  const extensionId = extensionIdFromPublicKey(publicKey);

  mkdirSync(dirname(keyPath), { recursive: true });
  // flag 'wx': fail rather than overwrite, even if the file appeared meanwhile.
  writeFileSync(keyPath, privateKey, { mode: 0o600, flag: 'wx' });

  const updated = { ...config, extensionId, publicKey: publicKey.toString('base64') };
  writeFileSync(configPath, JSON.stringify(updated, null, 2) + '\n');

  return { extensionId, publicKey: updated.publicKey, keyPath, fingerprint: fingerprint(publicKey) };
}

/** A short SHA-256 of the public key, to tell keys apart in the output. */
function fingerprint(publicKey) {
  return createHash('sha256').update(publicKey).digest('hex').slice(0, 16);
}

/** `owner/name` of the `origin` remote, or null when it isn't a GitHub remote. */
function originRepo() {
  try {
    const url = execFileSync('git', ['remote', 'get-url', 'origin'], { cwd: repoRoot, stdio: 'pipe' })
      .toString()
      .trim();
    return /github\.com[:/]([\w.-]+\/[\w.-]+?)(?:\.git)?$/.exec(url)?.[1] ?? null;
  } catch {
    return null;
  }
}

function parseArgs(argv) {
  const opts = { force: false };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--force') {
      opts.force = true;
      continue;
    }
    const m = /^--(out|repo)(?:=(.*))?$/.exec(a);
    if (!m) throw new Error(`Unknown argument "${a}".`);
    opts[m[1]] = m[2] ?? argv[++i];
  }
  return opts;
}

/** Run the CLI: make the key, print the ID and the next steps. */
function main(argv) {
  const opts = parseArgs(argv);
  const out = opts.out?.replace(/^~(?=$|[\\/])/, homedir());
  const result = createTestRingKey({ out, force: opts.force });
  const repo = opts.repo ?? originRepo();
  const updateUrl = repo
    ? testRingUrls(repo).updateUrl
    : 'https://github.com/<owner>/<repo>/releases/download/extension-test-ring/updates.xml';

  console.log(`Test-ring extension ID: ${result.extensionId}`);
  console.log(`Private key (keep it secret, never commit it): ${result.keyPath}`);
  console.log(`Public key fingerprint: ${result.fingerprint}`);
  console.log('Wrote the ID and public key to extension/test-ring.json.');
  console.log('');
  console.log('Next (docs/RELEASE.md "Extension test ring"):');
  console.log(`  1. Store the private key as the ${KEY_ENV} repository secret. From bash:`);
  console.log(`       gh secret set ${KEY_ENV} < "${result.keyPath}"`);
  console.log('     or from PowerShell:');
  console.log(`       gh secret set ${KEY_ENV} --body (Get-Content "${result.keyPath}" -Raw)`);
  console.log('     Keep a copy in your offline secret store, then delete this file.');
  console.log('  2. Commit extension/test-ring.json and release the agent with it.');
  console.log('  3. Once the test laptops run that agent release, publish the test ring and');
  console.log('     create the Intune test-ring profile with this forcelist entry:');
  console.log(`       ${result.extensionId};${updateUrl}`);
}

if (isMainModule(import.meta.url, process.argv[1])) {
  try {
    main(process.argv.slice(2));
  } catch (e) {
    console.error(`error: ${e.message}`);
    process.exitCode = 1;
  }
}
