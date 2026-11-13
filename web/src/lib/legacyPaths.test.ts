import { describe, expect, it } from 'vitest';
import { legacyPath } from './legacyPaths';
import { pathAfterOrgSwitch } from './orgSwitch';
import { SECTIONS, sectionOf } from '../nav';

describe('legacyPath', () => {
  it('moves the old sections and keeps the filter', () => {
    expect(legacyPath('/fuel', '?from=2026-10-01&to=2026-10-31')).toBe('/sales/fuel?from=2026-10-01&to=2026-10-31');
    expect(legacyPath('/items', '')).toBe('/sales/shop');
    expect(legacyPath('/finance', '?stations=a')).toBe('/money/margin?stations=a');
    expect(legacyPath('/costs', '')).toBe('/money/costs');
    expect(legacyPath('/tanks/', '')).toBe('/stock/tanks');
    expect(legacyPath('/data', '')).toBe('/stations/data');
    expect(legacyPath('/settings/profile', '')).toBe('/profile');
    expect(legacyPath('/settings/organizations', '')).toBe('/platform/organizations');
  });

  it('turns the old station tabs into sections', () => {
    expect(legacyPath('/stations', '?tab=connect')).toBe('/platform/connect');
    expect(legacyPath('/stations', '?tab=compare&from=2026-10-01')).toBe('/sales/stations?from=2026-10-01');
    expect(legacyPath('/stations', '?tab=updates')).toBe('/platform/updates');
  });

  it('leaves current addresses alone', () => {
    for (const p of ['/', '/shifts', '/shifts/0b6e1c1e-1111-4222-8333-444455556666/12', '/sales/fuel', '/stations', '/stations/data', '/settings/users', '/help/changes', '/profile'])
      expect(legacyPath(p, '?from=2026-10-01')).toBeNull();
  });

  it('never points to a section that does not exist', () => {
    for (const p of ['/fuel', '/demand', '/items', '/plans', '/finance', '/costs', '/tanks', '/data', '/settings/organizations']) {
      const to = legacyPath(p, '')!;
      const s = sectionOf(to)!;
      expect(s, to).toBeDefined();
      const tab = to.slice(s.path.length + 1);
      expect(s.tabs?.map((t) => t.key), to).toContain(tab);
    }
  });
});

describe('sections', () => {
  it('find the section of a path', () => {
    expect(sectionOf('/')?.label).toBe('Сводка');
    expect(sectionOf('/sales/shop')?.label).toBe('Продажи');
    expect(sectionOf('/shifts/x/1')?.label).toBe('Смены');
    expect(sectionOf('/salesman')).toBeUndefined();
  });

  it('have unique paths and tab keys', () => {
    expect(new Set(SECTIONS.map((s) => s.path)).size).toBe(SECTIONS.length);
    for (const s of SECTIONS) expect(new Set(s.tabs?.map((t) => t.key)).size).toBe(s.tabs?.length ?? 0);
  });

  it('organization switch keeps the section tab', () => {
    expect(pathAfterOrgSwitch('/sales/fuel')).toBe('/sales/fuel');
    expect(pathAfterOrgSwitch('/shifts/0b6e1c1e-1111-4222-8333-444455556666/12')).toBe('/shifts');
  });
});
