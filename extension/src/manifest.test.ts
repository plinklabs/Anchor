import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';

const manifestUrl = new URL('./manifest.json', import.meta.url);
const resolveFromManifest = (rel: string) =>
  fileURLToPath(new URL(rel, manifestUrl));

/** Width/height from a PNG's IHDR chunk (bytes 16-23, big-endian). */
function pngSize(buf: Buffer): { width: number; height: number } {
  return { width: buf.readUInt32BE(16), height: buf.readUInt32BE(20) };
}

// Issue #123: the extension id must be stable across every machine so the school
// can pin Anchor by id in Edge policy (ExtensionInstallForcelist). The id is a
// pure function of the manifest `key` (the base64 SPKI public key), so locking
// `key` here locks the id. This regression test fails if `key` is dropped or
// regenerated — which would silently break every deployed policy entry.
//
// The canonical id is documented in extension/README.md ("Stable extension ID").
const STABLE_EXTENSION_ID = 'akkfdaclmpfcnjalcifkcbhgjnnopman';

const manifest = JSON.parse(readFileSync(fileURLToPath(manifestUrl), 'utf8')) as {
  key?: string;
  icons?: Record<string, string>;
  action?: { default_icon?: Record<string, string> };
};

/** Derive the Edge/Chrome extension id from a base64 SPKI public key, exactly as
 *  the browser does: sha256 of the DER bytes, first 16 bytes, hex, each hex
 *  digit 0-f mapped to a-p. */
function deriveExtensionId(keyBase64: string): string {
  const der = Buffer.from(keyBase64, 'base64');
  return createHash('sha256')
    .update(der)
    .digest()
    .subarray(0, 16)
    .toString('hex')
    .split('')
    .map((c) => String.fromCharCode('a'.charCodeAt(0) + parseInt(c, 16)))
    .join('');
}

describe('manifest key (stable extension id)', () => {
  it('ships a public key so the id is deterministic across machines', () => {
    expect(manifest.key).toBeTypeOf('string');
    expect(manifest.key).not.toHaveLength(0);
  });

  it('derives the documented stable extension id', () => {
    expect(deriveExtensionId(manifest.key!)).toBe(STABLE_EXTENSION_ID);
  });

  it('produces a well-formed 32-char extension id', () => {
    expect(STABLE_EXTENSION_ID).toMatch(/^[a-p]{32}$/);
  });
});

// Issue #163 (AF2): the extension ships the Anchor brand icons for the toolbar
// action and the store/management listing. Each declared icon must exist and be
// a square PNG of the size its manifest key promises, so the build never copies
// a missing or mis-sized asset.
describe('brand icons (AF2 / #163)', () => {
  it('declares store/management icons and a toolbar action icon', () => {
    expect(manifest.icons).toBeTypeOf('object');
    expect(manifest.action?.default_icon).toBeTypeOf('object');
    // The 128px icon is what the store/management page uses — it must exist.
    expect(manifest.icons!['128']).toBeTypeOf('string');
  });

  const declared: Record<string, string> = {
    ...manifest.icons,
    ...manifest.action?.default_icon,
  };

  for (const [size, rel] of Object.entries(declared)) {
    it(`icon "${size}" (${rel}) exists and is ${size}×${size}`, () => {
      const buf = readFileSync(resolveFromManifest(rel));
      expect(pngSize(buf)).toEqual({ width: Number(size), height: Number(size) });
    });
  }
});
