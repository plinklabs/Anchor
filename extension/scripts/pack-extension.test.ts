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
// it statically. Extension CI (extension-ci.yml) runs this suite with a pinned
// actionlint installed (#412). Elsewhere the check is skipped where the binary
// isn't installed (local dev without actionlint), so it never produces a false
// failure; in CI a missing binary fails it, so a broken install can't skip it.
describe('extension-release workflow', () => {
  const hasActionlint = (() => {
    try {
      execFileSync('actionlint', ['-version'], { stdio: 'ignore', shell: true });
      return true;
    } catch {
      return false;
    }
  })();
  // GitHub Actions sets CI=true on every runner.
  const requireActionlint = process.env.CI === 'true';

  const workflowPath = (name: string) =>
    fileURLToPath(new URL(`../../.github/workflows/${name}`, import.meta.url));

  it.runIf(hasActionlint || requireActionlint)('passes actionlint', () => {
    expect(hasActionlint, 'actionlint must be on PATH in CI; extension-ci.yml installs it').toBe(true);
    // Both release-path workflows: the tag-triggered release and the scheduled
    // key-expiry warning it depends on (#336). Neither runs on a PR, so a typo
    // in either would surface only on the day it was needed.
    const workflows = [workflowPath('extension-release.yml'), workflowPath('edge-key-expiry.yml')];
    for (const workflow of workflows) expect(existsSync(workflow)).toBe(true);
    // Throws (non-zero exit) if actionlint finds problems.
    execFileSync('actionlint', workflows, { cwd: extensionDir, stdio: 'inherit', shell: true });
  });

  // #336: a failed publish must not be able to go quiet again. actionlint proves
  // the YAML is valid, not that the reporting is still wired, so pin the pieces
  // that make a failure visible — each of these silently disables it if dropped.
  describe('failure reporting stays wired (#336)', () => {
    // Normalise line endings: git checks these out CRLF on Windows and LF on
    // Linux, and JS treats `\r` as a line terminator (so `.` won't cross it).
    // Without this the assertions below pass in CI and fail on a dev box —
    // the same platform split that hid #339.
    const readWorkflow = (name: string) =>
      readFileSync(workflowPath(name), 'utf8').replace(/\r\n/g, '\n');
    const release = readWorkflow('extension-release.yml');

    it('diagnoses a failed publish', () => {
      expect(release).toContain('node scripts/diagnose-publish-failure.mjs');
      // Must run *because* the publish failed — an unconditional step would
      // probe the store on every green release.
      expect(release).toMatch(/if:.*failure\(\).*steps\.publish\.outcome == 'failure'/);
    });

    it('files an issue when the release fails', () => {
      expect(release).toMatch(/notify:/);
      expect(release).toContain('gh issue create');
      // Without issues: write the job fails at the point it would report.
      expect(release).toMatch(/permissions:\s*\n\s*issues: write/);
    });

    it('checks key expiry before publishing, not after', () => {
      const expiryAt = release.indexOf('check-key-expiry.mjs');
      const publishAt = release.indexOf('wdzeng/edge-addon');
      expect(expiryAt).toBeGreaterThan(-1);
      expect(expiryAt).toBeLessThan(publishAt);
    });

    it('warns on a schedule, not only when someone cuts a release', () => {
      // A key expires on the calendar; the repo may go weeks without a release.
      const expiry = readWorkflow('edge-key-expiry.yml');
      expect(expiry).toMatch(/schedule:\s*\n\s*(#.*\n\s*)*- cron:/);
      expect(expiry).toContain('node scripts/check-key-expiry.mjs');
    });
  });

  // #401: a re-run keeps the secrets the run was created with (cli/cli#13522),
  // so after rotating EDGE_ADDONS_* only a fresh run on the tag can publish.
  // Without a manual trigger that would take a new tag.
  describe('fresh run after a secret change (#401)', () => {
    const release = readFileSync(workflowPath('extension-release.yml'), 'utf8').replace(/\r\n/g, '\n');
    const onBlock = release.slice(release.indexOf('\non:\n'), release.indexOf('\npermissions:'));

    it('can be started by hand as well as by a tag push', () => {
      expect(onBlock).toMatch(/^ {2}push:\n {4}tags:\n {6}- 'extension-v\*'$/m);
      expect(onBlock).toMatch(/^ {2}workflow_dispatch:/m);
    });

    it('refuses a manual run on anything but an extension tag, before building', () => {
      // `gh workflow run` without --ref runs on the default branch (develop);
      // that must stop at the first step, not package and publish develop.
      const guardAt = release.indexOf("if: ${{ !startsWith(github.ref, 'refs/tags/extension-v') }}");
      expect(guardAt).toBeGreaterThan(-1);
      expect(guardAt).toBeLessThan(release.indexOf('uses: actions/checkout@v4'));
      expect(release.slice(guardAt, release.indexOf('uses: actions/checkout@v4'))).toContain('exit 1');
      // A refused dispatch names no stranded tag, so it must not file an issue.
      expect(release).toMatch(/if: \$\{\{ failure\(\) && startsWith\(github\.ref, 'refs\/tags\/extension-v'\) \}\}/);
    });

    it('tells the filed issue to start a fresh run after rotating, and re-run only after a review', () => {
      const body = release.slice(release.indexOf('body="'), release.indexOf('_Filed automatically'));
      const items = body.split(/\n(?=\s*- \*\*)/);
      const freshItem = items.find((item) => item.includes('gh workflow run extension-release.yml --ref $TAG'));
      const rerunItem = items.find((item) => item.includes('gh run rerun'));
      expect(freshItem).toMatch(/credentials rejected/i);
      // The re-run sits in the in-review item, and only there.
      expect(rerunItem).toMatch(/in review/i);
      expect(rerunItem).not.toMatch(/credential/i);
    });
  });
});
