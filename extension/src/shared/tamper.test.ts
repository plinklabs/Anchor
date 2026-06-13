import { describe, it, expect } from 'vitest';
import { classifyCreatedWindow, isHostAccessLoss } from './tamper';

describe('classifyCreatedWindow', () => {
  it('flags an incognito window as inprivate_opened', () => {
    expect(classifyCreatedWindow({ incognito: true })).toBe('inprivate_opened');
  });

  it('ignores an ordinary window', () => {
    expect(classifyCreatedWindow({ incognito: false })).toBeNull();
  });

  it('ignores a window with no incognito flag', () => {
    expect(classifyCreatedWindow({})).toBeNull();
  });

  it('tolerates null/undefined — the event can fire without a window object', () => {
    expect(classifyCreatedWindow(null)).toBeNull();
    expect(classifyCreatedWindow(undefined)).toBeNull();
  });
});

describe('isHostAccessLoss', () => {
  it('treats a removal carrying origins as host-access loss', () => {
    expect(isHostAccessLoss({ origins: ['<all_urls>'] })).toBe(true);
  });

  it('ignores an API-permission-only removal (no origins)', () => {
    expect(isHostAccessLoss({ origins: [] })).toBe(false);
    expect(isHostAccessLoss({})).toBe(false);
  });

  it('tolerates null/undefined defensively', () => {
    expect(isHostAccessLoss(null)).toBe(false);
    expect(isHostAccessLoss(undefined)).toBe(false);
  });
});
