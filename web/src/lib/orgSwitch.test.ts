import { describe, expect, it } from 'vitest';
import { pathAfterOrgSwitch } from './orgSwitch';

describe('pathAfterOrgSwitch', () => {
  it('keeps a section page', () => {
    expect(pathAfterOrgSwitch('/finance')).toBe('/finance');
    expect(pathAfterOrgSwitch('/help/changes')).toBe('/help/changes');
    expect(pathAfterOrgSwitch('/')).toBe('/');
  });
  it('leaves a page of one station for its section', () => {
    expect(pathAfterOrgSwitch('/shifts/d6e0a689-471c-4638-b2f3-3e89c71b1edb/83')).toBe('/shifts');
    expect(pathAfterOrgSwitch('/d6e0a689-471c-4638-b2f3-3e89c71b1edb')).toBe('/');
  });
});
