import { describe, it, expect } from 'vitest';
// @ts-expect-error — .mjs sibling, no type declarations needed for the test.
import {
  API_ROOT,
  PROBE_OPERATION_ID,
  classifyProbeStatus,
  diagnose,
  formatDiagnosis,
} from './diagnose-publish-failure.mjs';
// @ts-expect-error — .mjs sibling, no type declarations needed for the test.
import {
  KEY_LIFETIME_DAYS,
  DEFAULT_WARN_WITHIN_DAYS,
  keyStatus,
  formatKeyStatus,
} from './check-key-expiry.mjs';

// #336: a failed extension release used to say only "403" — indistinguishable
// from the store's in-review block, which needs the opposite response (wait vs
// rotate credentials). These tests lock the two diagnostics that now tell a
// maintainer which one they are looking at, because the situation they describe
// is unreproducible on demand: the real signal comes from a third-party store
// API that needs live Partner Center credentials and would submit to a public
// listing. So the network is injected and every branch is driven here.

const MS_PER_DAY = 24 * 60 * 60 * 1000;
const ROTATED = '2026-09-07';
const rotatedPlus = (days: number) => new Date(Date.parse(`${ROTATED}T00:00:00Z`) + days * MS_PER_DAY);

/** A fetch stand-in that records its call and answers with `status`. */
function fakeFetch(status: number) {
  const calls: { url: string; init: { headers: Record<string, string> } }[] = [];
  const impl = (url: string, init: { headers: Record<string, string> }) => {
    calls.push({ url, init });
    return Promise.resolve({ status });
  };
  return { impl, calls };
}

describe('publish failure classification (#336)', () => {
  it('reads 401/403 as rejected credentials', () => {
    expect(classifyProbeStatus(401)).toBe('credentials');
    expect(classifyProbeStatus(403)).toBe('credentials');
  });

  it('reads 404 and 2xx as "auth worked, the store refused the submission"', () => {
    // A bogus operation id that resolves to 404 proves the request got past
    // authentication — which is the whole point of the probe.
    expect(classifyProbeStatus(404)).toBe('store');
    expect(classifyProbeStatus(200)).toBe('store');
    expect(classifyProbeStatus(202)).toBe('store');
  });

  it('refuses to guess on any other status', () => {
    expect(classifyProbeStatus(400)).toBe('unknown');
    expect(classifyProbeStatus(500)).toBe('unknown');
    expect(classifyProbeStatus(0)).toBe('unknown');
  });

  it('probes the documented endpoint with v1.1 auth headers', async () => {
    const { impl, calls } = fakeFetch(404);
    await diagnose({
      productId: 'prod-1',
      clientId: 'client-1',
      apiKey: 'key-1',
      fetchImpl: impl,
    });

    expect(calls).toHaveLength(1);
    expect(calls[0].url).toBe(
      `${API_ROOT}/v1/products/prod-1/submissions/draft/package/operations/${PROBE_OPERATION_ID}`,
    );
    // v1.1 authenticates with an ApiKey header plus X-ClientID — a Bearer token
    // is the retired v1 scheme and would 401 against this endpoint.
    expect(calls[0].init.headers.Authorization).toBe('ApiKey key-1');
    expect(calls[0].init.headers['X-ClientID']).toBe('client-1');
  });

  it('tells a credentials failure to rotate BOTH secrets', async () => {
    const { impl } = fakeFetch(403);
    const d = await diagnose({
      productId: 'p',
      clientId: 'c',
      apiKey: 'k',
      version: '0.4.1',
      runId: '123',
      fetchImpl: impl,
    });

    expect(d.cause).toBe('credentials');
    expect(d.probeStatus).toBe(403);
    // The trap this exists to prevent: updating only the API key. Partner
    // Center regenerates the Client ID alongside it.
    const recovery = d.recovery.join(' ');
    expect(recovery).toContain('EDGE_ADDONS_CLIENT_ID');
    expect(recovery).toContain('EDGE_ADDONS_API_KEY');
    expect(recovery).toContain('gh run rerun 123 --failed');
  });

  it('tells an in-review block to wait, not to touch the credentials', async () => {
    const { impl } = fakeFetch(404);
    const d = await diagnose({ productId: 'p', clientId: 'c', apiKey: 'k', fetchImpl: impl });

    expect(d.cause).toBe('store');
    expect(d.detail).toContain('in-review');
    expect(d.recovery.join(' ')).not.toContain('Create API credentials');
  });

  it('stays honest when the probe itself fails', async () => {
    const d = await diagnose({
      productId: 'p',
      clientId: 'c',
      apiKey: 'k',
      fetchImpl: () => Promise.reject(new Error('getaddrinfo ENOTFOUND')),
    });

    expect(d.cause).toBe('unknown');
    expect(d.probeStatus).toBeNull();
    expect(d.probeError).toContain('ENOTFOUND');
    // Both recoveries stay on the table rather than a confident wrong answer.
    expect(d.recovery.join(' ')).toContain('401/403');
    expect(d.recovery.join(' ')).toContain('in-progress submission');
  });

  it('renders an Actions error annotation', async () => {
    const { impl } = fakeFetch(403);
    const out = formatDiagnosis(await diagnose({ productId: 'p', clientId: 'c', apiKey: 'k', fetchImpl: impl }));
    expect(out).toContain('::error title=Edge Add-ons publish failed::');
    expect(out).toContain('Probe status: 403');
  });
});

describe('API key expiry tracking (#336)', () => {
  it('pins the 72-day lifetime Microsoft enforces', () => {
    // Not configurable and not extended by use — see check-key-expiry.mjs.
    expect(KEY_LIFETIME_DAYS).toBe(72);
  });

  it('reports plenty of life on a fresh key', () => {
    const s = keyStatus(ROTATED, rotatedPlus(0));
    expect(s.level).toBe('ok');
    expect(s.daysLeft).toBe(72);
    expect(s.expiresOn).toBe('2026-11-18');
  });

  it('starts warning exactly at the threshold, not before', () => {
    expect(keyStatus(ROTATED, rotatedPlus(KEY_LIFETIME_DAYS - DEFAULT_WARN_WITHIN_DAYS - 1)).level).toBe('ok');
    const atThreshold = keyStatus(ROTATED, rotatedPlus(KEY_LIFETIME_DAYS - DEFAULT_WARN_WITHIN_DAYS));
    expect(atThreshold.level).toBe('warn');
    expect(atThreshold.daysLeft).toBe(DEFAULT_WARN_WITHIN_DAYS);
  });

  it('still warns (not errors) on the day it lapses', () => {
    const s = keyStatus(ROTATED, rotatedPlus(KEY_LIFETIME_DAYS));
    expect(s.level).toBe('warn');
    expect(s.daysLeft).toBe(0);
  });

  it('reports an expired key with how long it has been dead', () => {
    const s = keyStatus(ROTATED, rotatedPlus(KEY_LIFETIME_DAYS + 3));
    expect(s.level).toBe('expired');
    expect(s.daysLeft).toBe(-3);
    expect(s.message).toContain('3 day(s) ago');
    expect(s.message).toContain('EDGE_ADDONS_CLIENT_ID');
  });

  it('says so when the rotation date is missing or malformed', () => {
    for (const bad of ['', '   ', 'yesterday', '07-09-2026', undefined]) {
      const s = keyStatus(bad as string, new Date());
      expect(s.level).toBe('unknown');
      expect(s.daysLeft).toBeNull();
    }
  });

  it('annotates expired as an error and warn/unknown as warnings', () => {
    expect(formatKeyStatus(keyStatus(ROTATED, rotatedPlus(99)))).toContain('::error title=');
    expect(formatKeyStatus(keyStatus(ROTATED, rotatedPlus(70)))).toContain('::warning title=');
    expect(formatKeyStatus(keyStatus('', new Date()))).toContain('::warning title=');
    // A healthy key must not emit an annotation — otherwise every green release
    // run carries a warning and the real ones stop standing out.
    expect(formatKeyStatus(keyStatus(ROTATED, rotatedPlus(1)))).not.toContain('::');
  });
});
