import { describe, it, expect } from 'vitest';
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

// #412: no workflow ran this suite, so the release-path checks in it (actionlint
// over the tag-only workflows, the #336 and #401 pins) only ever ran on a
// developer's machine, and a broken unit test or release workflow passed every
// PR check. Extension CI (extension-ci.yml) runs it now; pin what makes it work.

const extensionDir = fileURLToPath(new URL('..', import.meta.url));
// Normalise line endings: git checks workflows out CRLF on Windows (see #339).
const readWorkflow = (name: string) =>
  readFileSync(
    fileURLToPath(new URL(`../../.github/workflows/${name}`, import.meta.url)),
    'utf8',
  ).replace(/\r\n/g, '\n');

const ci = readWorkflow('extension-ci.yml');

/** The quoted `paths:` entries of one trigger in the workflow's `on:` block. */
function triggerPaths(yaml: string, trigger: 'pull_request' | 'push'): string[] {
  const start = yaml.indexOf('\non:\n');
  expect(start).toBeGreaterThan(-1);
  const rest = yaml.slice(start);
  // The block ends at the next top-level key.
  const on = rest.slice(0, rest.slice(1).search(/\n[^\s#]/) + 2);
  const block = on.match(new RegExp(`\\n {2}${trigger}:\\n((?: {4,}.*\\n)*)`))?.[1] ?? '';
  const paths = block.match(/^ {4}paths:\n((?: {6}- .*\n)*)/m)?.[1] ?? '';
  return [...paths.matchAll(/- '([^']+)'/g)].map((m) => m[1]);
}

/** Workflow files the unit tests read: the .yml names in any test file that
 *  opens .github/workflows. An edit to one of them must run this suite. */
function workflowsReadByTests(): string[] {
  const names = new Set<string>();
  for (const dir of ['src', 'scripts']) {
    const files = readdirSync(join(extensionDir, dir), { recursive: true, encoding: 'utf8' });
    for (const file of files.filter((f) => f.endsWith('.test.ts'))) {
      const source = readFileSync(join(extensionDir, dir, file), 'utf8');
      if (!source.includes('.github/workflows')) continue;
      for (const m of source.matchAll(/['"`]([\w.-]+\.ya?ml)['"`]/g)) names.add(m[1]);
    }
  }
  return [...names].sort();
}

describe('Extension CI runs the unit suite (#412)', () => {
  it('runs `npm test` in extension/ on pull requests and pushes that touch it', () => {
    expect(ci).toMatch(/^ {6}run:\n {8}working-directory: extension$/m);
    expect(ci).toMatch(/^ {8}run: npm test$/m);

    const pr = triggerPaths(ci, 'pull_request');
    expect(pr).toContain('extension/**');
    expect(pr).toContain('.github/workflows/extension-ci.yml');
    // Same paths for both, so a push can't skip what a PR would have run.
    expect(triggerPaths(ci, 'push')).toEqual(pr);
  });

  it('also runs when a workflow the tests check changes', () => {
    // The release workflows have no pull_request trigger of their own, so a PR
    // that edits one is checked only if it runs the tests that read it.
    const read = workflowsReadByTests();
    expect(read).toEqual(expect.arrayContaining(['extension-release.yml', 'edge-key-expiry.yml']));
    const pr = triggerPaths(ci, 'pull_request');
    for (const name of read) expect(pr).toContain(`.github/workflows/${name}`);
  });

  it('installs a pinned, checksum-verified actionlint before the tests run', () => {
    // pack-extension.test.ts lints the release workflows with it, and requires
    // it under CI rather than skipping.
    expect(ci).toMatch(/ACTIONLINT_VERSION: '\d+\.\d+\.\d+'/);
    expect(ci).toMatch(/ACTIONLINT_SHA256: [0-9a-f]{64}\n/);
    expect(ci).toContain('sha256sum -c -');
    expect(ci).toContain('>> "$GITHUB_PATH"');
    expect(ci.indexOf('name: Install actionlint')).toBeGreaterThan(-1);
    expect(ci.indexOf('name: Install actionlint')).toBeLessThan(ci.indexOf('run: npm test'));
  });
});
