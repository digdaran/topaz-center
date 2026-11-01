import { describe, expect, it } from 'vitest';
import { versionAtLeast } from './version';

describe('versionAtLeast', () => {
  it('orders versions numerically', () => {
    expect(versionAtLeast('1.10.0', '1.9.0')).toBe(true);
    expect(versionAtLeast('1.2.0', '1.2.0')).toBe(true);
    expect(versionAtLeast('1.1.9', '1.2.0')).toBe(false);
  });
  it('puts pre-releases before the release', () => {
    expect(versionAtLeast('1.2.0-rc.1', '1.2.0')).toBe(false);
    expect(versionAtLeast('1.2.0', '1.2.0-rc.1')).toBe(true);
    expect(versionAtLeast('1.2.0-rc.10', '1.2.0-rc.2')).toBe(true);
  });
  it('treats missing versions as not up to date', () => {
    expect(versionAtLeast(null, '1.2.0')).toBe(false);
    expect(versionAtLeast('1.2.0', null)).toBe(false);
  });
});
