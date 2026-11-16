// Minimal, dependency-free CRX3 writer for the self-hosted test-ring build (#497).
//
// Edge installs a self-hosted extension from a signed CRX3 package: a small
// header, then the same ZIP the store gets. The header carries our RSA public key
// and a signature over the ZIP, and Edge derives the extension's ID from that key,
// so the key fixes the test-ring build's ID the way the store's key fixes the
// store build's. Node's crypto does the RSA work; the header is three protobuf
// messages, written by hand below, the same reason zip.mjs exists: no packaging
// dependency, identical output on every OS.
//
// Format (Chromium components/crx_file, crx3.proto):
//
//   "Cr24" | uint32le 3 | uint32le header length | CrxFileHeader | ZIP
//
//   CrxFileHeader { repeated AsymmetricKeyProof sha256_with_rsa = 2;
//                   bytes signed_header_data = 10000; }
//   AsymmetricKeyProof { bytes public_key = 1; bytes signature = 2; }
//   SignedData { bytes crx_id = 1; }   // the bytes in signed_header_data
//
// crx_id is the first 16 bytes of SHA-256 over the DER (SPKI) public key; the ID
// Edge shows is those bytes in hex with each digit 0–f written as a letter a–p.
// The signature is RSASSA-PKCS1-v1_5 with SHA-256 over
//   "CRX3 SignedData\x00" | uint32le len(signed_header_data) | signed_header_data | ZIP

import { createHash, createPrivateKey, createPublicKey, sign } from 'node:crypto';

const MAGIC = Buffer.from('Cr24', 'ascii');
const FORMAT_VERSION = 3;
/** What Chromium prefixes the signed bytes with. */
export const SIGNATURE_CONTEXT = Buffer.from('CRX3 SignedData\x00', 'latin1');

/** Protobuf base-128 varint. */
function varint(value) {
  const out = [];
  let n = value;
  while (n > 0x7f) {
    out.push((n & 0x7f) | 0x80);
    n >>>= 7;
  }
  out.push(n);
  return Buffer.from(out);
}

/** A length-delimited protobuf field (wire type 2). */
function bytesField(number, bytes) {
  return Buffer.concat([varint(number * 8 + 2), varint(bytes.length), bytes]);
}

/** The DER (SPKI) public key of an RSA private key given as PEM. */
export function publicKeyDer(privateKeyPem) {
  return createPublicKey(createPrivateKey(privateKeyPem)).export({ type: 'spki', format: 'der' });
}

/** The 16-byte CRX id of a DER (SPKI) public key. */
export function crxId(publicKey) {
  return createHash('sha256').update(publicKey).digest().subarray(0, 16);
}

/**
 * The extension ID Edge gives a CRX signed with this DER (SPKI) public key, or an
 * unpacked extension whose manifest `key` is its base64. Same derivation as the
 * store ID's (src/manifest.test.ts).
 */
export function extensionIdFromPublicKey(publicKey) {
  return [...crxId(publicKey).toString('hex')]
    .map((digit) => String.fromCharCode(97 + parseInt(digit, 16)))
    .join('');
}

/**
 * Sign `zip` (the packaged extension) with the RSA private key `privateKeyPem`
 * into a CRX3 package. Deterministic: PKCS#1 v1.5 signatures have no random
 * part, so the same ZIP and key always give the same bytes.
 */
export function packCrx3(zip, privateKeyPem) {
  const key = createPrivateKey(privateKeyPem);
  if (key.asymmetricKeyType !== 'rsa') {
    throw new Error(`The test-ring signing key must be an RSA key (got ${key.asymmetricKeyType}).`);
  }
  const publicKey = createPublicKey(key).export({ type: 'spki', format: 'der' });

  const signedHeaderData = bytesField(1, crxId(publicKey));
  const signedLength = Buffer.alloc(4);
  signedLength.writeUInt32LE(signedHeaderData.length);
  const signature = sign(
    'sha256',
    Buffer.concat([SIGNATURE_CONTEXT, signedLength, signedHeaderData, zip]),
    key,
  );

  const header = Buffer.concat([
    bytesField(2, Buffer.concat([bytesField(1, publicKey), bytesField(2, signature)])),
    bytesField(10000, signedHeaderData),
  ]);
  const prefix = Buffer.alloc(12);
  MAGIC.copy(prefix, 0);
  prefix.writeUInt32LE(FORMAT_VERSION, 4);
  prefix.writeUInt32LE(header.length, 8);
  return Buffer.concat([prefix, header, zip]);
}
