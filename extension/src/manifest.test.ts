import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';

// Issue #123: the extension id must be stable across every machine so the school
// can pin Anchor by id in Edge policy (ExtensionInstallForcelist). The id is a
// pure function of the manifest `key` (the base64 SPKI public key), so locking
// `key` here locks the id. This regression test fails if `key` is dropped or
// regenerated — which would silently break every deployed policy entry.
//
// The canonical id is documented in extension/README.md ("Stable extension ID").
const STABLE_EXTENSION_ID = 'akkfdaclmpfcnjalcifkcbhgjnnopman';

const manifest = JSON.parse(
  readFileSync(fileURLToPath(new URL('./manifest.json', import.meta.url)), 'utf8'),
) as { key?: string };

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
