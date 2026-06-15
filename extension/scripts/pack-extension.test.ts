import { describe, it, expect, beforeAll } from 'vitest';
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, statSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { inflateRawSync } from 'node:zlib';
// @ts-expect-error — .mjs sibling, no type declarations needed for the test.
import { packExtension } from './pack-extension.mjs';

// #210: the tag-triggered Edge Add-ons release packages dist/ into a ZIP. These
// tests lock the packaging core (pack-extension.mjs) so the release path can't
// silently ship a mis-versioned package, ship a `key` the Edge store rejects, or
// produce a corrupt archive. They drive the *real* build (rollup) once, then the
// real packaging — the same code the workflow runs — and read the produced ZIP
// back with Node's own inflate, so a green test means the actual artifact is
// sound (an end-to-end check of the pack step, the user-visible deliverable here).

const extensionDir = fileURLToPath(new URL('..', import.meta.url));
const pkg = JSON.parse(
  readFileSync(new URL('../package.json', import.meta.url), 'utf8'),
) as { version: string };

/** Minimal single-file extractor for the ZIPs makeZip produces (DEFLATE entries,
 *  no data descriptors). Walks the central directory and inflates the named
 *  entry — enough to verify the archive is well-formed and read a file back. */
function readZipEntries(zip: Buffer): Map<string, Buffer> {
  // Find EOCD (22 bytes, fixed since we write no comment).
  const eocd = zip.length - 22;
  expect(zip.readUInt32LE(eocd)).toBe(0x06054b50);
  const count = zip.readUInt16LE(eocd + 10);
  let p = zip.readUInt32LE(eocd + 16); // central dir offset
  const out = new Map<string, Buffer>();
  for (let i = 0; i < count; i++) {
    expect(zip.readUInt32LE(p)).toBe(0x02014b50); // central header sig
    const method = zip.readUInt16LE(p + 10);
    const compSize = zip.readUInt32LE(p + 20);
    const nameLen = zip.readUInt16LE(p + 28);
    const extraLen = zip.readUInt16LE(p + 30);
    const commentLen = zip.readUInt16LE(p + 32);
    const localOff = zip.readUInt32LE(p + 42);
    const name = zip.toString('utf8', p + 46, p + 46 + nameLen);

    // Jump to the local header to find where the data starts.
    expect(zip.readUInt32LE(localOff)).toBe(0x04034b50);
    const lNameLen = zip.readUInt16LE(localOff + 26);
    const lExtraLen = zip.readUInt16LE(localOff + 28);
    const dataStart = localOff + 30 + lNameLen + lExtraLen;
    const raw = zip.subarray(dataStart, dataStart + compSize);
    out.set(name, method === 8 ? inflateRawSync(raw) : Buffer.from(raw));

    p += 46 + nameLen + extraLen + commentLen;
  }
  return out;
}

describe('pack-extension (#210 Edge Add-ons packaging)', () => {
  let zipPath: string;
  let entries: Map<string, Buffer>;

  beforeAll(() => {
    // Real build + real package — the exact path the release workflow runs.
    zipPath = packExtension({ version: pkg.version, build: true });
    entries = readZipEntries(readFileSync(zipPath));
  }, 120_000);

  it('writes a versioned zip named from package.json', () => {
    expect(existsSync(zipPath)).toBe(true);
    expect(zipPath.replace(/\\/g, '/')).toMatch(
      new RegExp(`/artifacts/anchor-extension-${pkg.version.replace(/\./g, '\\.')}\\.zip$`),
    );
    expect(statSync(zipPath).size).toBeGreaterThan(0);
  });

  it('packages the stamped manifest at the archive root, version-locked to package.json', () => {
    const manifestBuf = entries.get('manifest.json');
    expect(manifestBuf, 'manifest.json must be at the zip root').toBeDefined();
    const manifest = JSON.parse(manifestBuf!.toString('utf8')) as {
      version?: string;
      key?: string;
    };
    expect(manifest.version).toBe(pkg.version);
  });

  it('strips `key` from the packaged manifest so the Edge store accepts the upload', () => {
    // The store assigns the extension ID and rejects a manifest carrying `key`
    // ("The manifest shouldn't contain the key field"). dist/manifest.json keeps
    // `key` for unpacked dev; the packaged copy must not.
    const manifest = JSON.parse(entries.get('manifest.json')!.toString('utf8')) as {
      key?: string;
    };
    expect(manifest.key).toBeUndefined();
  });

  it('includes the runtime bundle and brand icons (a loadable extension, not just a manifest)', () => {
    const names = [...entries.keys()];
    expect(names).toContain('background.js');
    expect(names.some((n) => n.startsWith('icons/'))).toBe(true);
    // The 128px icon is the one the store listing renders.
    expect(names).toContain('icons/icon-128.png');
  });

  it('rejects a tag version that does not match the committed source version', () => {
    expect(() => packExtension({ version: '999.0.0', build: false })).toThrow(
      /does not match the committed single version source/,
    );
  });
});

// Guard the YAML the release ships in: a malformed workflow would never run on a
// tag, and the PR gate doesn't execute tag-only workflows. actionlint validates
// it statically. Skipped automatically where the binary isn't installed (local
// dev without actionlint) so it never produces a false failure; CI has it.
describe('extension-release workflow', () => {
  const hasActionlint = (() => {
    try {
      execFileSync('actionlint', ['-version'], { stdio: 'ignore', shell: true });
      return true;
    } catch {
      return false;
    }
  })();

  it.runIf(hasActionlint)('passes actionlint', () => {
    const workflow = fileURLToPath(
      new URL('../../.github/workflows/extension-release.yml', import.meta.url),
    );
    expect(existsSync(workflow)).toBe(true);
    // Throws (non-zero exit) if actionlint finds problems.
    execFileSync('actionlint', [workflow], { cwd: extensionDir, stdio: 'inherit', shell: true });
  });
});
