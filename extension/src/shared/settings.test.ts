import { describe, it, expect, beforeEach, vi } from 'vitest';
import {
  DEV_FALLBACK_BACKEND_URL,
  loadSettings,
  persistAuthConfig,
  persistBackendUrl,
  resolveAuthMode,
  type ExtensionSettings,
} from './settings';

// Minimal in-memory chrome.storage.local so the settings helpers can be
// exercised without a real extension context.
function installChromeStorageMock(seed: Record<string, unknown> = {}): Record<string, unknown> {
  const store: Record<string, unknown> = { ...seed };
  vi.stubGlobal('chrome', {
    storage: {
      local: {
        get: async (keys: string | string[]) => {
          const list = Array.isArray(keys) ? keys : [keys];
          const out: Record<string, unknown> = {};
          for (const k of list) out[k] = store[k];
          return out;
        },
        set: async (items: Record<string, unknown>) => {
          Object.assign(store, items);
        },
      },
    },
  });
  return store;
}

describe('loadSettings', () => {
  beforeEach(() => installChromeStorageMock());

  it('falls back to the dev default backend url when nothing is stored', async () => {
    const settings = await loadSettings();
    expect(settings.backendUrl).toBe(DEV_FALLBACK_BACKEND_URL);
    expect(settings.devImpersonateOid).toBeNull();
  });

  it('does NOT bake in a production backend url (only the local dev port)', async () => {
    // Guards #204: the prod default must stay out of source. The only default is
    // the local dev port — a real deployment learns its URL from the agent.
    expect(DEV_FALLBACK_BACKEND_URL).toBe('http://localhost:5276');
  });

  it('uses the stored backend url (what the agent pushed) over the default', async () => {
    installChromeStorageMock({ backendUrl: 'https://anchor.school.example' });
    const settings = await loadSettings();
    expect(settings.backendUrl).toBe('https://anchor.school.example');
  });

  it('trims a trailing slash off the stored url', async () => {
    installChromeStorageMock({ backendUrl: 'https://anchor.example/' });
    const settings = await loadSettings();
    expect(settings.backendUrl).toBe('https://anchor.example');
  });
});

describe('persistBackendUrl', () => {
  it('stores a fresh url and reports it changed', async () => {
    const store = installChromeStorageMock();
    const changed = await persistBackendUrl('https://anchor.example');
    expect(changed).toBe(true);
    expect(store.backendUrl).toBe('https://anchor.example');
  });

  it('trims a trailing slash before storing', async () => {
    const store = installChromeStorageMock();
    await persistBackendUrl('https://anchor.example/');
    expect(store.backendUrl).toBe('https://anchor.example');
  });

  it('is a no-op (returns false) when the url is unchanged', async () => {
    installChromeStorageMock({ backendUrl: 'https://anchor.example' });
    // Trailing slash + whitespace must still count as "same".
    const changed = await persistBackendUrl('  https://anchor.example/  ');
    expect(changed).toBe(false);
  });

  it('rejects a blank url so a malformed host message cannot wipe config', async () => {
    const store = installChromeStorageMock({ backendUrl: 'https://anchor.example' });
    const changed = await persistBackendUrl('   ');
    expect(changed).toBe(false);
    expect(store.backendUrl).toBe('https://anchor.example');
  });
});

const FULL_AUTH = { tenantId: 'tenant-1', clientId: 'client-1', scope: 'api://x/.default' };

describe('loadSettings authConfig (#289)', () => {
  beforeEach(() => installChromeStorageMock());

  it('is null when nothing is stored', async () => {
    expect((await loadSettings()).authConfig).toBeNull();
  });

  it('loads a complete stored auth config', async () => {
    installChromeStorageMock({ authConfig: FULL_AUTH });
    expect((await loadSettings()).authConfig).toEqual(FULL_AUTH);
  });

  it('treats a partial stored auth config as null (no half-configured sign-in)', async () => {
    installChromeStorageMock({ authConfig: { tenantId: 't', clientId: '', scope: 's' } });
    expect((await loadSettings()).authConfig).toBeNull();
  });
});

describe('persistAuthConfig (#289)', () => {
  it('stores a fresh config and reports it changed', async () => {
    const store = installChromeStorageMock();
    expect(await persistAuthConfig(FULL_AUTH)).toBe(true);
    expect(store.authConfig).toEqual(FULL_AUTH);
  });

  it('trims fields before storing', async () => {
    const store = installChromeStorageMock();
    await persistAuthConfig({ tenantId: '  tenant-1  ', clientId: '  client-1  ', scope: '  api://x/.default  ' });
    expect(store.authConfig).toEqual(FULL_AUTH);
  });

  it('is a no-op (returns false) when unchanged', async () => {
    installChromeStorageMock({ authConfig: FULL_AUTH });
    expect(await persistAuthConfig(FULL_AUTH)).toBe(false);
  });

  it('rejects an incomplete config so a malformed host message cannot half-configure auth', async () => {
    const store = installChromeStorageMock({ authConfig: FULL_AUTH });
    expect(await persistAuthConfig({ tenantId: 't', clientId: 'c', scope: '   ' })).toBe(false);
    expect(store.authConfig).toEqual(FULL_AUTH);
  });
});

describe('resolveAuthMode (#289)', () => {
  const base: ExtensionSettings = { backendUrl: 'https://x', devImpersonateOid: null, authConfig: null };

  it('prefers the dev impersonation shortcut when set, even alongside an auth config', () => {
    expect(resolveAuthMode({ ...base, devImpersonateOid: 'oid', authConfig: FULL_AUTH })).toBe('dev');
  });

  it('uses token mode when only an auth config is present', () => {
    expect(resolveAuthMode({ ...base, authConfig: FULL_AUTH })).toBe('token');
  });

  it('is none when neither is configured', () => {
    expect(resolveAuthMode(base)).toBe('none');
  });
});
