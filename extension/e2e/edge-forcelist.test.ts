// #429: the e2e harness's check for an Edge forcelist entry that force-installs
// Anchor, and the key-less copy it loads instead. A Vitest unit test, not a
// Playwright spec: it needs no browser, so it also runs on the Linux CI runner,
// where detection has to be a no-op.

import { describe, it, expect, afterEach } from 'vitest';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {
  FORCELIST_KEYS,
  anchorEntriesIn,
  describeEntry,
  extensionIdForPath,
  findForcelistEntry,
  stageKeylessCopy,
} from './edge-forcelist.ts';

const ANCHOR_ID = 'dnkimhodjfogjibnbbfdjdapgmmiojio';
const OTHER_ID = 'someotherextensionidaaaaaaaaaaaa';
const UPDATE_URL = 'https://edge.microsoft.com/extensionwebstorebase/v1/crx';
const [HKLM_KEY, HKCU_KEY] = FORCELIST_KEYS;

/** `reg query <key>` output for a key with these values, as reg.exe prints it. */
function regOutput(key: string, values: Record<string, string>): string {
  const hive = key.replace(/^HKLM\\/, 'HKEY_LOCAL_MACHINE\\').replace(/^HKCU\\/, 'HKEY_CURRENT_USER\\');
  const lines = Object.entries(values).map(([name, data]) => `    ${name}    REG_SZ    ${data}`);
  return ['', hive, ...lines, '', ''].join('\r\n');
}

describe('anchorEntriesIn', () => {
  it('finds the entry the agent writes (#405)', () => {
    const out = regOutput(HKLM_KEY, { '1': `${ANCHOR_ID};${UPDATE_URL}` });
    expect(anchorEntriesIn(HKLM_KEY, out, ANCHOR_ID)).toEqual([
      { key: HKLM_KEY, name: '1', data: `${ANCHOR_ID};${UPDATE_URL}` },
    ]);
  });

  it('matches the id segment the way the agent does: bare, any case, padded', () => {
    const out = regOutput(HKLM_KEY, {
      '1': ANCHOR_ID,
      '2': `${ANCHOR_ID.toUpperCase()};https://x`,
      '3': ` ${ANCHOR_ID} ;https://x`,
    });
    expect(anchorEntriesIn(HKLM_KEY, out, ANCHOR_ID).map((e) => e.name)).toEqual(['1', '2', '3']);
  });

  it("ignores other extensions' entries and the key's default value", () => {
    const out = [
      regOutput(HKLM_KEY, { '1': `${OTHER_ID};${UPDATE_URL}`, '2': `${ANCHOR_ID}x;https://x` }),
      '    (Default)    REG_SZ    (value not set)',
    ].join('\r\n');
    expect(anchorEntriesIn(HKLM_KEY, out, ANCHOR_ID)).toEqual([]);
  });
});

describe('findForcelistEntry', () => {
  it('is a no-op off Windows: it reads nothing and finds nothing', () => {
    let queried = 0;
    const query = () => {
      queried++;
      return regOutput(HKLM_KEY, { '1': ANCHOR_ID });
    };
    for (const platform of ['linux', 'darwin'] as const) {
      expect(findForcelistEntry(ANCHOR_ID, { platform, query })).toBeNull();
    }
    expect(queried).toBe(0);
  });

  it('finds nothing where neither key exists (the CI runner)', () => {
    const asked: string[] = [];
    const entry = findForcelistEntry(ANCHOR_ID, {
      platform: 'win32',
      query: (key) => {
        asked.push(key);
        return null;
      },
    });
    expect(entry).toBeNull();
    expect(asked).toEqual([HKLM_KEY, HKCU_KEY]);
  });

  it('finds nothing where the forcelist only names other extensions', () => {
    const query = (key: string) => regOutput(key, { '1': `${OTHER_ID};${UPDATE_URL}` });
    expect(findForcelistEntry(ANCHOR_ID, { platform: 'win32', query })).toBeNull();
  });

  it('finds the HKLM entry, before any HKCU one', () => {
    const query = (key: string) =>
      regOutput(key, key === HKLM_KEY ? { '1': OTHER_ID, '2': ANCHOR_ID } : { '1': ANCHOR_ID });
    expect(findForcelistEntry(ANCHOR_ID, { platform: 'win32', query })).toEqual({
      key: HKLM_KEY,
      name: '2',
      data: ANCHOR_ID,
    });
  });

  it('finds an HKCU-only entry (IT policy, older agents)', () => {
    const query = (key: string) => (key === HKCU_KEY ? regOutput(key, { '4': ANCHOR_ID }) : null);
    expect(findForcelistEntry(ANCHOR_ID, { platform: 'win32', query })).toEqual({
      key: HKCU_KEY,
      name: '4',
      data: ANCHOR_ID,
    });
  });

  it('describes an entry by key, slot and value', () => {
    expect(describeEntry({ key: HKLM_KEY, name: '1', data: ANCHOR_ID })).toBe(
      `${HKLM_KEY} "1" = ${ANCHOR_ID}`,
    );
  });
});

describe('extensionIdForPath', () => {
  // The id real Edge 155 gave a key-less copy of dist/ loaded from this path.
  const EDGE_LOADED = { dir: 'C:\\Users\\Public\\anchor-e2e-vector\\dist', id: 'odgcgbkdinnppppfmifpjgkaghmgimlm' };

  it('derives the id Edge gives a key-less unpacked extension from its path', () => {
    expect(extensionIdForPath(EDGE_LOADED.dir)).toBe(EDGE_LOADED.id);
  });

  it('upper-cases the drive letter first, as Edge does', () => {
    expect(extensionIdForPath(`c${EDGE_LOADED.dir.slice(1)}`)).toBe(EDGE_LOADED.id);
  });

  it('gives a different, well-formed id per path, never the pinned one', () => {
    const id = extensionIdForPath('C:\\Temp\\anchor-ext-e2e-keyless-1234');
    expect(id).toMatch(/^[a-p]{32}$/);
    expect(id).not.toBe(EDGE_LOADED.id);
    expect(id).not.toBe(ANCHOR_ID);
  });
});

describe('stageKeylessCopy', () => {
  let root: string | null = null;
  afterEach(() => {
    if (root) fs.rmSync(root, { recursive: true, force: true });
    root = null;
  });

  it('copies dist/ without the manifest key and replaces an earlier copy', () => {
    root = fs.mkdtempSync(path.join(os.tmpdir(), 'anchor-keyless-test-'));
    const dist = path.join(root, 'dist');
    fs.mkdirSync(path.join(dist, 'icons'), { recursive: true });
    fs.writeFileSync(
      path.join(dist, 'manifest.json'),
      JSON.stringify({ manifest_version: 3, name: 'Anchor', version: '1.2.3', key: 'MIIB' }),
    );
    fs.writeFileSync(path.join(dist, 'background.js'), 'console.log(1);');
    fs.writeFileSync(path.join(dist, 'icons', 'icon-16.png'), 'png');

    const copy = path.join(root, 'keyless');
    fs.mkdirSync(copy);
    fs.writeFileSync(path.join(copy, 'stale.js'), 'old build');

    stageKeylessCopy(dist, copy);

    expect(JSON.parse(fs.readFileSync(path.join(copy, 'manifest.json'), 'utf8'))).toEqual({
      manifest_version: 3,
      name: 'Anchor',
      version: '1.2.3',
    });
    expect(fs.readFileSync(path.join(copy, 'background.js'), 'utf8')).toBe('console.log(1);');
    expect(fs.existsSync(path.join(copy, 'icons', 'icon-16.png'))).toBe(true);
    expect(fs.existsSync(path.join(copy, 'stale.js'))).toBe(false);
    // dist/ itself keeps its key: packaging and CI load it as built.
    expect(JSON.parse(fs.readFileSync(path.join(dist, 'manifest.json'), 'utf8')).key).toBe('MIIB');
  });
});
