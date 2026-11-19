import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { ALL_SITES, extensionPageUrl, runSelfCheck, type SelfCheckApi } from './self-check';

function api(allSites: boolean | Error, inPrivate: boolean | Error): SelfCheckApi {
  const answer = (value: boolean | Error) => () =>
    value instanceof Error ? Promise.reject(value) : Promise.resolve(value);
  return { containsAllSites: answer(allSites), isAllowedInPrivate: answer(inPrivate) };
}

describe('runSelfCheck', () => {
  it('reports both settings as the browser reads them', async () => {
    expect(await runSelfCheck(api(true, true))).toEqual({ allSites: true, inPrivate: true });
    expect(await runSelfCheck(api(false, false))).toEqual({ allSites: false, inPrivate: false });
  });

  it('reports site access taken away while InPrivate stays as it was', async () => {
    expect(await runSelfCheck(api(false, true))).toEqual({ allSites: false, inPrivate: true });
  });

  it('counts a setting it cannot read as fine, and says which one failed', async () => {
    const failed: string[] = [];
    const result = await runSelfCheck(api(new Error('gone'), false), (setting) => failed.push(setting));
    expect(result).toEqual({ allSites: true, inPrivate: false });
    expect(failed).toEqual(['site access']);
  });
});

describe('ALL_SITES', () => {
  it('is the host access the manifest asks for', () => {
    const manifest = JSON.parse(readFileSync(new URL('../manifest.json', import.meta.url), 'utf8')) as {
      host_permissions: string[];
    };
    expect(ALL_SITES.origins).toEqual(manifest.host_permissions);
  });
});

describe('extensionPageUrl', () => {
  it("points at the extension's own page in the extension settings", () => {
    expect(extensionPageUrl('dnkimhodjfogjibnbbfdjdapgmmiojio')).toBe(
      'chrome://extensions/?id=dnkimhodjfogjibnbbfdjdapgmmiojio',
    );
  });
});
