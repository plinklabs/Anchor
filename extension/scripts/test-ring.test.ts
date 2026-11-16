import { describe, it, expect, beforeAll, afterAll } from 'vitest';
import { execFileSync } from 'node:child_process';
import { createHash, generateKeyPairSync, verify } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { inflateRawSync } from 'node:zlib';
// @ts-expect-error — .mjs sibling, no type declarations needed for the test.
import { SIGNATURE_CONTEXT, extensionIdFromPublicKey, packCrx3 } from './crx3.mjs';
import {
  compareVersions,
  packTestRing,
  publishedVersion,
  testRingUrls,
  testRingVersion,
  updateManifest,
  // @ts-expect-error — .mjs sibling, no type declarations needed for the test.
} from './pack-test-ring.mjs';
// @ts-expect-error — .mjs sibling, no type declarations needed for the test.
import { createTestRingKey } from './test-ring-key.mjs';
// @ts-expect-error — .mjs sibling, no type declarations needed for the test.
import { makeZip } from './zip.mjs';

// #497: the self-hosted test-ring build. Edge force-installs it on the test
// laptops from our own update URL, so nothing reviews or re-signs it on the way:
// the CRX3 we write is exactly what Edge verifies and installs. These tests read
// every artifact back the way Edge does — the CRX header checked with the same
// rules as Chromium's verifier (components/crx_file/crx_verifier.cc), the
// manifest read out of the signed archive, the update manifest parsed — against a
// throwaway key generated here. Edge's acceptance of an off-store forcelist entry
// itself needs a domain-joined or MDM-enrolled laptop, which is the manual check
// in docs/INTUNE.md.

const extensionDir = fileURLToPath(new URL('..', import.meta.url));
const repoRoot = fileURLToPath(new URL('../..', import.meta.url));
const pkg = JSON.parse(readFileSync(join(extensionDir, 'package.json'), 'utf8')) as { version: string };
const REPO = 'plinklabs/Anchor';

type KeyPair = { privateKeyPem: string; publicKey: Buffer; id: string };

function throwawayKey(): KeyPair {
  const { publicKey, privateKey } = generateKeyPairSync('rsa', {
    modulusLength: 2048,
    publicKeyEncoding: { type: 'spki', format: 'der' },
    privateKeyEncoding: { type: 'pkcs8', format: 'pem' },
  });
  return { privateKeyPem: privateKey, publicKey, id: extensionIdFromPublicKey(publicKey) };
}

/** Protobuf length-delimited fields of `buf`, as [field number, bytes]. */
function protoFields(buf: Buffer): [number, Buffer][] {
  const out: [number, Buffer][] = [];
  let p = 0;
  const varint = () => {
    let value = 0;
    let shift = 0;
    for (;;) {
      const b = buf[p++];
      value += (b & 0x7f) * 2 ** shift;
      if (!(b & 0x80)) return value;
      shift += 7;
    }
  };
  while (p < buf.length) {
    const key = varint();
    expect(key % 8, 'every CRX3 header field is length-delimited').toBe(2);
    const len = varint();
    out.push([Math.floor(key / 8), buf.subarray(p, p + len)]);
    p += len;
  }
  return out;
}

/**
 * Verify a CRX3 the way Chromium does: magic and version, a SignedData naming a
 * crx_id, an RSA proof whose key hashes to that crx_id, and every RSA signature
 * valid over the signed header data and the archive. Returns the ID and archive.
 */
function verifyCrx3(crx: Buffer): { id: string; archive: Buffer } {
  expect(crx.subarray(0, 4).toString('latin1')).toBe('Cr24');
  expect(crx.readUInt32LE(4)).toBe(3);
  const headerLength = crx.readUInt32LE(8);
  const header = crx.subarray(12, 12 + headerLength);
  const archive = crx.subarray(12 + headerLength);

  const fields = protoFields(header);
  const signedHeaderData = fields.find(([n]) => n === 10000)?.[1];
  expect(signedHeaderData, 'signed_header_data').toBeDefined();
  const crxId = protoFields(signedHeaderData!).find(([n]) => n === 1)?.[1];
  expect(crxId?.length).toBe(16);

  const proofs = fields.filter(([n]) => n === 2).map(([, proof]) => {
    const parts = protoFields(proof);
    return {
      publicKey: parts.find(([n]) => n === 1)![1],
      signature: parts.find(([n]) => n === 2)![1],
    };
  });
  expect(proofs.length).toBeGreaterThan(0);
  const length = Buffer.alloc(4);
  length.writeUInt32LE(signedHeaderData!.length);
  const signed = Buffer.concat([SIGNATURE_CONTEXT, length, signedHeaderData!, archive]);
  for (const { publicKey, signature } of proofs) {
    expect(
      verify('sha256', signed, { key: publicKey, format: 'der', type: 'spki' }, signature),
      'RSA signature over the archive',
    ).toBe(true);
  }
  // The developer key: the proof whose key the crx_id is derived from.
  const developer = proofs.find(
    ({ publicKey }) => createHash('sha256').update(publicKey).digest().subarray(0, 16).equals(crxId!),
  );
  expect(developer, 'a proof by the key the crx_id names').toBeDefined();
  return { id: extensionIdFromPublicKey(developer!.publicKey), archive };
}

/** The entries of a ZIP that makeZip wrote, inflated. */
function readZip(zip: Buffer): Map<string, Buffer> {
  const eocd = zip.length - 22;
  expect(zip.readUInt32LE(eocd)).toBe(0x06054b50);
  const out = new Map<string, Buffer>();
  let p = zip.readUInt32LE(eocd + 16);
  for (let i = 0; i < zip.readUInt16LE(eocd + 10); i++) {
    const compressed = zip.readUInt32LE(p + 20);
    const nameLength = zip.readUInt16LE(p + 28);
    const local = zip.readUInt32LE(p + 42);
    const name = zip.toString('utf8', p + 46, p + 46 + nameLength);
    const start = local + 30 + zip.readUInt16LE(local + 26) + zip.readUInt16LE(local + 28);
    out.set(name, inflateRawSync(zip.subarray(start, start + compressed)));
    p += 46 + nameLength + zip.readUInt16LE(p + 30) + zip.readUInt16LE(p + 32);
  }
  return out;
}

const temps: string[] = [];
afterAll(() => {
  for (const dir of temps) rmSync(dir, { recursive: true, force: true });
});

function tempDir(): string {
  const dir = mkdtempSync(join(tmpdir(), 'anchor-test-ring-'));
  temps.push(dir);
  return dir;
}

function writeConfig(dir: string, extensionId: string, publicKey: string): string {
  const path = join(dir, 'test-ring.json');
  writeFileSync(path, JSON.stringify({ $comment: 'test', extensionId, publicKey }, null, 2) + '\n');
  return path;
}

describe('CRX3 signing (#497)', () => {
  const key = throwawayKey();
  const zip = makeZip([{ name: 'manifest.json', data: Buffer.from('{"manifest_version":3}') }]);

  it('derives the ID Edge gives the store key, the same way as the store ID', () => {
    const manifest = JSON.parse(readFileSync(join(extensionDir, 'src', 'manifest.json'), 'utf8'));
    expect(extensionIdFromPublicKey(Buffer.from(manifest.key, 'base64'))).toBe('dnkimhodjfogjibnbbfdjdapgmmiojio');
  });

  it("writes a CRX3 that Chromium's verifier accepts, under the key's ID", () => {
    const { id, archive } = verifyCrx3(packCrx3(zip, key.privateKeyPem));
    expect(id).toBe(key.id);
    expect(archive.equals(zip)).toBe(true);
  });

  it('signs the archive: a changed byte breaks the signature', () => {
    const crx = packCrx3(zip, key.privateKeyPem);
    crx[crx.length - 30] ^= 0xff;
    expect(() => verifyCrx3(crx)).toThrow(/RSA signature/);
  });

  it('is deterministic, so a re-run publishes the same bytes', () => {
    expect(packCrx3(zip, key.privateKeyPem).equals(packCrx3(zip, key.privateKeyPem))).toBe(true);
  });

  // The strongest check short of a managed laptop: Edge's own packer
  // (`msedge --pack-extension`) signs an extension, and ours, given the archive
  // Edge zipped and the same key, must write the identical bytes. Runs wherever
  // Edge is installed (a Windows dev box); the Linux CI runner has none.
  const edge = [process.env['ProgramFiles(x86)'], process.env.ProgramFiles]
    .filter((dir): dir is string => !!dir)
    .map((dir) => join(dir, 'Microsoft', 'Edge', 'Application', 'msedge.exe'))
    .find((exe) => existsSync(exe));

  it.runIf(process.platform === 'win32' && edge)('writes the very bytes Edge’s own packer writes', () => {
    const dir = tempDir();
    const source = join(dir, 'ext');
    mkdirSync(source);
    writeFileSync(join(source, 'manifest.json'), '{"manifest_version":3,"name":"crx check","version":"1.0"}');
    writeFileSync(join(source, 'background.js'), 'console.log(1);\n');
    writeFileSync(join(dir, 'key.pem'), key.privateKeyPem);

    execFileSync(
      edge!,
      [
        `--user-data-dir=${join(dir, 'profile')}`,
        '--no-first-run',
        '--no-message-box',
        `--pack-extension=${source}`,
        `--pack-extension-key=${join(dir, 'key.pem')}`,
      ],
      { stdio: 'ignore', timeout: 60_000 },
    );

    const edgeCrx = readFileSync(join(dir, 'ext.crx'));
    const { id, archive } = verifyCrx3(edgeCrx);
    expect(id).toBe(key.id);
    expect(packCrx3(archive, key.privateKeyPem).equals(edgeCrx)).toBe(true);
  }, 90_000);
});

describe('pack-test-ring (#497)', () => {
  const key = throwawayKey();
  let dir: string;
  let configPath: string;
  let distDir: string;
  let outDir: string;
  let result: {
    crxPath: string;
    updatesPath: string;
    extensionId: string;
    version: string;
    updateUrl: string;
    crxUrl: string;
  };
  let entries: Map<string, Buffer>;
  let crxId: string;

  beforeAll(() => {
    dir = tempDir();
    configPath = writeConfig(dir, key.id, key.publicKey.toString('base64'));
    outDir = join(dir, 'out');
    // A build of its own, shaped like dist/ (the store manifest stamped with the
    // package version, a script, an icon): pack-extension.test.ts rebuilds the real
    // dist/ in parallel, and reading it mid-build would make this flaky. The
    // packaging is the exact code the workflow runs after `npm run build`.
    distDir = join(dir, 'dist');
    mkdirSync(join(distDir, 'icons'), { recursive: true });
    const storeManifest = JSON.parse(readFileSync(join(extensionDir, 'src', 'manifest.json'), 'utf8'));
    writeFileSync(join(distDir, 'manifest.json'), JSON.stringify({ ...storeManifest, version: pkg.version }, null, 2));
    writeFileSync(join(distDir, 'background.js'), 'console.log("anchor");\n');
    writeFileSync(join(distDir, 'icons', 'icon-128.png'), readFileSync(join(extensionDir, 'src', 'icons', 'icon-128.png')));

    result = packTestRing({
      privateKeyPem: key.privateKeyPem,
      repo: REPO,
      buildNumber: '12',
      version: pkg.version,
      build: false,
      distDir,
      configPath,
      outDir,
    });
    const verified = verifyCrx3(readFileSync(result.crxPath));
    crxId = verified.id;
    entries = readZip(verified.archive);
  });

  it('signs the CRX with the key whose ID test-ring.json names', () => {
    expect(crxId).toBe(key.id);
    expect(result.extensionId).toBe(key.id);
    expect(result.crxPath.replace(/\\/g, '/')).toMatch(
      new RegExp(`/anchor-extension-test-ring-${pkg.version.replace(/\./g, '\\.')}\\.12\\.crx$`),
    );
  });

  it('packages a manifest that updates from our update manifest, newer with every build', () => {
    const manifest = JSON.parse(entries.get('manifest.json')!.toString('utf8'));
    expect(manifest.version).toBe(`${pkg.version}.12`);
    expect(manifest.update_url).toBe(
      `https://github.com/${REPO}/releases/download/extension-test-ring/updates.xml`,
    );
    expect(manifest.update_url).toBe(result.updateUrl);
    // The test-ring key, not the store's, so even an unpacked load has the test-ring ID.
    expect(extensionIdFromPublicKey(Buffer.from(manifest.key, 'base64'))).toBe(key.id);
    expect(manifest.manifest_version).toBe(3);
  });

  it('packages the whole build, byte for byte apart from the manifest', () => {
    expect([...entries.keys()].sort()).toEqual(['background.js', 'icons/icon-128.png', 'manifest.json']);
    expect(entries.get('icons/icon-128.png')!.equals(readFileSync(join(distDir, 'icons', 'icon-128.png')))).toBe(true);
  });

  it('writes the update manifest Edge polls, naming this CRX and version', () => {
    const xml = readFileSync(result.updatesPath, 'utf8');
    expect(xml).toContain("<gupdate xmlns='http://www.google.com/update2/response' protocol='2.0'>");
    expect(xml).toContain(`<app appid='${key.id}'>`);
    expect(xml).toContain(
      `<updatecheck codebase='https://github.com/${REPO}/releases/download/extension-test-ring/` +
        `anchor-extension-test-ring-${pkg.version}.12.crx' version='${pkg.version}.12' />`,
    );
    expect(result.crxUrl.endsWith(result.crxPath.replace(/\\/g, '/').split('/').at(-1)!)).toBe(true);
  });

  it('leaves the store build alone', () => {
    const dist = JSON.parse(readFileSync(join(distDir, 'manifest.json'), 'utf8'));
    expect(dist.version).toBe(pkg.version);
    expect(dist.update_url).toBeUndefined();
    expect(extensionIdFromPublicKey(Buffer.from(dist.key, 'base64'))).toBe('dnkimhodjfogjibnbbfdjdapgmmiojio');
  });

  it("refuses a key whose ID the agent wouldn't take, before writing anything", () => {
    const other = throwawayKey();
    const out = join(dir, 'refused');
    const pack = (config: string) => () =>
      packTestRing({ privateKeyPem: other.privateKeyPem, repo: REPO, buildNumber: 13, configPath: config, outDir: out, build: false });

    expect(pack(configPath)).toThrow(new RegExp(`signs as ${other.id}, but extension/test-ring.json names ${key.id}`));
    expect(pack(writeConfig(tempDir(), '', ''))).toThrow(/names no test-ring ID yet/);
    expect(existsSync(out)).toBe(false);
  });

  it('refuses a version older than the published one, and republishes the same one', () => {
    const pack = (published: string) =>
      packTestRing({
        privateKeyPem: key.privateKeyPem,
        repo: REPO,
        buildNumber: 12,
        configPath,
        outDir: join(dir, 'again'),
        build: false,
        distDir,
        publishedXml: updateManifest({ extensionId: key.id, crxUrl: 'https://x/y.crx', version: published }),
      });

    expect(() => pack('999.0.0.1')).toThrow(/already has 999\.0\.0\.1, newer than/);
    // A re-run of the same workflow run has the same build number and bytes.
    expect(pack(`${pkg.version}.12`).version).toBe(`${pkg.version}.12`);
    expect(pack(`${pkg.version}.11`).version).toBe(`${pkg.version}.12`);
  });

  it('refuses a tag version that does not match package.json, and a missing key', () => {
    const base = { repo: REPO, buildNumber: 1, configPath, outDir: join(dir, 'x'), build: false };
    expect(() => packTestRing({ ...base, privateKeyPem: key.privateKeyPem, version: '999.0.0' })).toThrow(
      /does not match the committed single version source/,
    );
    expect(() => packTestRing({ ...base, privateKeyPem: '' })).toThrow(/EXTENSION_TEST_RING_KEY/);
  });

  it('as the workflow runs it, fails loudly while the repo names no test-ring ID or the secret is empty', () => {
    // The real CLI, the committed test-ring.json. Until the key exists that file is
    // empty, so a secret set too early can't publish a build no agent accepts.
    const committed = JSON.parse(readFileSync(join(extensionDir, 'test-ring.json'), 'utf8'));
    const cli = (env: Record<string, string>) => {
      try {
        execFileSync(
          process.execPath,
          ['scripts/pack-test-ring.mjs', '--repo', REPO, '--build-number', '7', '--version', '', '--no-build'],
          { cwd: extensionDir, env: { ...process.env, ...env }, stdio: 'pipe' },
        );
        return '';
      } catch (e) {
        return String((e as { stderr: Buffer }).stderr);
      }
    };

    expect(cli({ EXTENSION_TEST_RING_KEY: '' })).toMatch(/No test-ring signing key: set EXTENSION_TEST_RING_KEY/);
    const wrongKey = cli({ EXTENSION_TEST_RING_KEY: throwawayKey().privateKeyPem });
    expect(wrongKey).toMatch(committed.extensionId ? /signs as [a-p]{32}, but/ : /names no test-ring ID yet/);
    expect(wrongKey).not.toMatch(/PRIVATE KEY/);
  });
});

describe('test-ring versions and URLs (#497)', () => {
  it('appends the build number, within what Edge allows', () => {
    expect(testRingVersion('0.4.4', 12)).toBe('0.4.4.12');
    expect(testRingVersion('0.4.4', '0')).toBe('0.4.4.0');
    expect(() => testRingVersion('0.4.4', 65536)).toThrow();
    expect(() => testRingVersion('0.4.4', '')).toThrow();
    expect(() => testRingVersion('0.4.4', undefined)).toThrow();
    expect(() => testRingVersion('0.4.4-beta', 1)).toThrow();
  });

  it('orders versions the way Edge does', () => {
    expect(compareVersions('0.4.4.12', '0.4.4.9')).toBe(1);
    expect(compareVersions('0.4.5.1', '0.4.4.900')).toBe(1);
    expect(compareVersions('0.4.4.12', '0.4.4.12')).toBe(0);
    expect(compareVersions('0.4.4', '0.4.4.0')).toBe(0);
    expect(compareVersions('0.4.3.99', '0.4.4.1')).toBe(-1);
  });

  it('reads the version a published update manifest offers', () => {
    expect(publishedVersion(updateManifest({ extensionId: 'a'.repeat(32), crxUrl: 'https://x', version: '1.2.3.4' }))).toBe(
      '1.2.3.4',
    );
    expect(publishedVersion('<gupdate/>')).toBeNull();
  });

  it('hosts the build on a release of its own repository', () => {
    expect(testRingUrls('some-school/Anchor').updateUrl).toBe(
      'https://github.com/some-school/Anchor/releases/download/extension-test-ring/updates.xml',
    );
    expect(() => testRingUrls('not a repo')).toThrow();
  });
});

describe('test-ring-key (#497)', () => {
  let dir: string;
  beforeAll(() => {
    dir = tempDir();
  });

  it('writes the private key outside the repo and only the public key and ID into test-ring.json', () => {
    const configPath = writeConfig(dir, '', '');
    const out = join(dir, 'keys', 'ring.pem');

    const result = createTestRingKey({ out, configPath, root: repoRoot });

    const pem = readFileSync(out, 'utf8');
    expect(pem).toMatch(/^-----BEGIN PRIVATE KEY-----/);
    const config = JSON.parse(readFileSync(configPath, 'utf8'));
    expect(config.$comment).toBe('test');
    expect(config.extensionId).toBe(result.extensionId);
    expect(extensionIdFromPublicKey(Buffer.from(config.publicKey, 'base64'))).toBe(config.extensionId);
    expect(readFileSync(configPath, 'utf8')).not.toMatch(/PRIVATE/);
    // The key it wrote is the one packaging accepts for that ID.
    const zip = makeZip([{ name: 'manifest.json', data: Buffer.from('{}') }]);
    expect(verifyCrx3(packCrx3(zip, pem)).id).toBe(config.extensionId);
  });

  it('refuses to write the private key inside the repository', () => {
    const configPath = writeConfig(dir, '', '');
    const inside = join(extensionDir, 'artifacts', 'ring.pem');
    expect(() => createTestRingKey({ out: inside, configPath, root: repoRoot })).toThrow(/inside the repository/);
    expect(existsSync(inside)).toBe(false);
    expect(JSON.parse(readFileSync(configPath, 'utf8')).extensionId).toBe('');
  });

  it('never overwrites a key file', () => {
    const configPath = writeConfig(tempDir(), '', '');
    createTestRingKey({ out: join(dir, 'a.pem'), configPath, root: repoRoot });
    const before = readFileSync(join(dir, 'a.pem'), 'utf8');

    expect(() =>
      createTestRingKey({ out: join(dir, 'a.pem'), configPath: writeConfig(tempDir(), '', ''), root: repoRoot }),
    ).toThrow(/already exists/);
    expect(readFileSync(join(dir, 'a.pem'), 'utf8')).toBe(before);
  });

  it('keeps a configured ID unless forced: a new key is a new ID every laptop policy names', () => {
    const configPath = writeConfig(tempDir(), '', '');
    const first = createTestRingKey({ out: join(dir, 'first.pem'), configPath, root: repoRoot });

    expect(() => createTestRingKey({ out: join(dir, 'second.pem'), configPath, root: repoRoot })).toThrow(
      /already names/,
    );
    expect(existsSync(join(dir, 'second.pem'))).toBe(false);
    expect(JSON.parse(readFileSync(configPath, 'utf8')).extensionId).toBe(first.extensionId);

    const second = createTestRingKey({ out: join(dir, 'second.pem'), configPath, root: repoRoot, force: true });
    expect(second.extensionId).not.toBe(first.extensionId);
    expect(JSON.parse(readFileSync(configPath, 'utf8')).extensionId).toBe(second.extensionId);
  });
});

describe('extension/test-ring.json, the one place the test-ring ID is configured (#497)', () => {
  const config = JSON.parse(readFileSync(join(extensionDir, 'test-ring.json'), 'utf8')) as {
    extensionId: string;
    publicKey: string;
  };

  it('names an ID and the public key it derives from, or neither', () => {
    if (config.extensionId === '' && config.publicKey === '') return;
    expect(config.extensionId).toMatch(/^[a-p]{32}$/);
    expect(extensionIdFromPublicKey(Buffer.from(config.publicKey, 'base64'))).toBe(config.extensionId);
    expect(config.extensionId).not.toBe('dnkimhodjfogjibnbbfdjdapgmmiojio');
  });

  it('is what the agent build embeds', () => {
    const csproj = readFileSync(join(repoRoot, 'agent', 'src', 'FocusAgent.Core', 'FocusAgent.Core.csproj'), 'utf8');
    expect(csproj).toMatch(
      /<EmbeddedResource Include="\.\.\\\.\.\\\.\.\\extension\\test-ring\.json"\s+LogicalName="FocusAgent\.Core\.Extension\.test-ring\.json"/,
    );
  });
});

describe('extension-test-ring workflow (#497)', () => {
  const workflowPath = fileURLToPath(new URL('../../.github/workflows/extension-test-ring.yml', import.meta.url));
  const workflow = readFileSync(workflowPath, 'utf8').replace(/\r\n/g, '\n');
  const onBlock = workflow.slice(workflow.indexOf('\non:\n'), workflow.indexOf('\npermissions:'));

  const hasActionlint = (() => {
    try {
      execFileSync('actionlint', ['-version'], { stdio: 'ignore', shell: true });
      return true;
    } catch {
      return false;
    }
  })();
  const requireActionlint = process.env.CI === 'true';

  it.runIf(hasActionlint || requireActionlint)('passes actionlint', () => {
    expect(hasActionlint, 'actionlint must be on PATH in CI; extension-ci.yml installs it').toBe(true);
    // Tag- and hand-triggered only, so no PR ever runs it: lint it here.
    execFileSync('actionlint', [workflowPath], { cwd: extensionDir, stdio: 'inherit', shell: true });
  });

  it('publishes only on an explicit trigger: an extension tag or a manual run', () => {
    // Never on a branch push, a PR or a schedule.
    expect(onBlock.trim()).toBe("on:\n  push:\n    tags:\n      - 'extension-v*'\n  workflow_dispatch:");
  });

  it('skips cleanly, without failing, while the signing key is not set', () => {
    // Secrets can't gate a job directly, so one job looks and the publish job
    // runs only on its answer: a skipped job, not a red run.
    expect(workflow).toMatch(/KEY: \$\{\{ secrets\.EXTENSION_TEST_RING_KEY \}\}/);
    expect(workflow).toMatch(/echo "configured=false" >> "\$GITHUB_OUTPUT"/);
    expect(workflow).toMatch(/if: needs\.check\.outputs\.configured == 'true'/);
    const check = workflow.slice(workflow.indexOf('\n  check:'), workflow.indexOf('\n  publish:'));
    expect(check).not.toMatch(/exit 1/);
  });

  it('hosts the build next to the agent feed as a pre-release, never Latest', () => {
    // The agent's update feed is the release GitHub marks Latest (#465); a
    // pre-release can never be that.
    expect(workflow).toMatch(/gh release create "\$TAG"[^\n]*\\\n(?:\s+--[^\n]*\n)*?\s+--prerelease/);
    expect(workflow).toMatch(/TAG: extension-test-ring/);
    expect(workflow).not.toMatch(/--latest(?!=false)/);
  });

  it('uploads the CRX before the update manifest that names it', () => {
    const crxAt = workflow.indexOf('gh release upload "$TAG" "$CRX"');
    const updatesAt = workflow.indexOf('gh release upload "$TAG" "$UPDATES"');
    expect(crxAt).toBeGreaterThan(-1);
    expect(crxAt).toBeLessThan(updatesAt);
  });

  it('packages with the script these tests cover, against the published manifest', () => {
    expect(workflow).toContain('node scripts/pack-test-ring.mjs');
    expect(workflow).toContain('--published');
    expect(workflow).toMatch(/EXTENSION_TEST_RING_KEY: \$\{\{ secrets\.EXTENSION_TEST_RING_KEY \}\}/);
  });
});
