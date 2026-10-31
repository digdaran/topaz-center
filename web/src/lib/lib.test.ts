import { describe, expect, it } from 'vitest';
import { bucketLabel, change, dateTime, liters, money, pct } from './format';
import { filterQuery, parseFilter, presets } from './filter';

const nbsp = (s: string) => s.replace(new RegExp('[\\u00a0\\u202f]', 'g'), ' ');

describe('format', () => {
  it('formats money, liters and shares the Russian way', () => {
    expect(nbsp(money(1950))).toBe('1 950,00 ₽');
    expect(nbsp(money(1234567.89))).toBe('1 234 568 ₽');
    expect(nbsp(money('94453.63', true))).toBe('94 453,63 ₽');
    expect(money(null)).toBe('—');
    expect(nbsp(liters(2080.25))).toBe('2 080 л');
    expect(nbsp(liters(23.03))).toBe('23,03 л');
    expect(nbsp(pct(0.4382))).toBe('43,8 %');
  });

  it('computes relative change only when comparable', () => {
    expect(change(110, 100)).toBeCloseTo(0.1);
    expect(change(5, 0)).toBeNull();
    expect(change(null, 10)).toBeNull();
  });

  it('shows timestamps in the station time zone', () => {
    // 18:59 UTC = 21:59 Moscow = 23:59 Yekaterinburg
    expect(dateTime('2026-10-31T18:59:52Z', 'Europe/Moscow')).toBe('31.10.2026, 21:59');
    expect(dateTime('2026-10-31T18:59:52Z', 'Asia/Yekaterinburg')).toBe('31.10.2026, 23:59');
  });

  it('labels series buckets', () => {
    expect(bucketLabel('2026-10-31T00:00', 'day')).toBe('31.10.2026');
    expect(bucketLabel('2026-10-31T14:00', 'hour')).toBe('31.10 14:00');
    expect(bucketLabel('2026-10-01T00:00', 'month')).toBe('окт 2026');
  });
});

describe('filter', () => {
  const now = new Date(2026, 9, 31);

  it('defaults to the current month', () => {
    const f = parseFilter(new URLSearchParams(''), now);
    expect(f).toMatchObject({ from: '2026-10-01', to: '2026-10-31', compare: 'prev', stations: [] });
  });

  it('round-trips through the query string and rejects garbage', () => {
    const f = parseFilter(new URLSearchParams('stations=a,b&from=2026-09-01&to=2026-09-30&g=week&compare=yoy&fuel=СУГ'), now);
    expect(f).toEqual({ stations: ['a', 'b'], from: '2026-09-01', to: '2026-09-30', g: 'week', compare: 'yoy', fuel: 'СУГ' });
    expect(parseFilter(new URLSearchParams(filterQuery(f)), now)).toEqual(f);
    const bad = parseFilter(new URLSearchParams('from=yesterday&g=year&compare=x'), now);
    expect(bad).toMatchObject({ from: '2026-10-01', g: undefined, compare: 'prev' });
  });

  it('swaps reversed periods', () => {
    expect(parseFilter(new URLSearchParams('from=2026-10-10&to=2026-10-01'), now)).toMatchObject({ from: '2026-10-01', to: '2026-10-10' });
  });

  it('builds presets relative to today', () => {
    const p = Object.fromEntries(presets(now).map((x) => [x.label, x]));
    expect(p['Прошлый месяц']).toMatchObject({ from: '2026-09-01', to: '2026-09-30' });
    expect(p['Вчера']).toMatchObject({ from: '2026-10-30', to: '2026-10-30' });
    expect(p['Эта неделя'].from).toBe('2026-10-26'); // 31.10.2026 is Saturday
  });
});
