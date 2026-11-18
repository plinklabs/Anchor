import { describe, it, expect } from 'vitest';
import { classifyCreatedWindow } from './tamper';

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
