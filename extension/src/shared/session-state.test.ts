import { describe, it, expect } from 'vitest';
import { mergeDomainsInto } from './session-state';
import type { ActiveSessionState, AllowedDomainDto } from './types';

const baseState = (domains: AllowedDomainDto[]): ActiveSessionState => ({
  sessionId: 'session-1',
  classId: 'class-1',
  mode: 'Strict',
  joinCode: '123456',
  startedAt: '2026-05-24T00:00:00Z',
  domains,
});

describe('mergeDomainsInto', () => {
  it('returns the same reference when nothing is added', () => {
    const state = baseState([{ matchType: 'Suffix', value: 'office.com' }]);
    const result = mergeDomainsInto(state, []);
    expect(result).toBe(state);
  });

  it('appends new domains', () => {
    const state = baseState([{ matchType: 'Suffix', value: 'office.com' }]);
    const result = mergeDomainsInto(state, [{ matchType: 'Suffix', value: 'reddit.com' }]);
    expect(result).not.toBe(state);
    expect(result.domains).toHaveLength(2);
    expect(result.domains[1]).toEqual({ matchType: 'Suffix', value: 'reddit.com' });
  });

  it('dedupes case-insensitively on (matchType, value)', () => {
    const state = baseState([{ matchType: 'Suffix', value: 'reddit.com' }]);
    const result = mergeDomainsInto(state, [{ matchType: 'Suffix', value: 'REDDIT.com' }]);
    // No change, so should return the original reference.
    expect(result).toBe(state);
  });

  it('treats Exact vs Suffix as different entries', () => {
    const state = baseState([{ matchType: 'Exact', value: 'reddit.com' }]);
    const result = mergeDomainsInto(state, [{ matchType: 'Suffix', value: 'reddit.com' }]);
    expect(result.domains).toHaveLength(2);
  });

  it('skips empty values', () => {
    const state = baseState([]);
    const result = mergeDomainsInto(state, [
      { matchType: 'Suffix', value: '' },
      { matchType: 'Suffix', value: 'reddit.com' },
    ]);
    expect(result.domains).toEqual([{ matchType: 'Suffix', value: 'reddit.com' }]);
  });
});
