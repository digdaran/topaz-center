import { describe, expect, it } from 'vitest';
import { changelog, guide, headings, render } from './help';
import { latestVersion, releases } from './version';

const num = (v: string) => v.split('.').map(Number).reduce((a, x) => a * 1000 + x, 0);

describe('help texts', () => {
  it('changelog lists releases newest first with dates', () => {
    const list = releases();
    expect(list.length).toBeGreaterThan(5);
    expect(latestVersion).toBe(list[0].version);
    for (let i = 1; i < list.length; i++) expect(num(list[i - 1].version)).toBeGreaterThan(num(list[i].version));
    // every "## " heading of the changelog is a release heading (a typo would hide a version)
    expect(changelog.match(/^## /gm)!.length).toBe(list.length);
  });

  it('guide renders with linkable headings', () => {
    const html = render(guide);
    const hs = headings(guide);
    expect(hs.length).toBeGreaterThan(10);
    for (const h of hs) expect(html).toContain(`id="${h.id}"`);
    expect(new Set(hs.map((h) => h.id)).size).toBe(hs.length);
  });
});
