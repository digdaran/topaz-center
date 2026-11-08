import { describe, expect, it } from 'vitest';
import { safeNext } from './safeNext';

describe('safeNext', () => {
  it('keeps paths of this site', () => {
    expect(safeNext('/shifts?from=2026-11-01')).toBe('/shifts?from=2026-11-01');
    expect(safeNext(null)).toBe('/');
  });
  it('refuses other sites', () => {
    for (const bad of ['//evil.example', '/\\evil.example', '/\\/evil.example', 'https://evil.example', 'evil', '/ok\\..\\x'])
      expect(safeNext(bad)).toBe('/');
  });
});
