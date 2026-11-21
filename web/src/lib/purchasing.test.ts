import { describe, expect, it } from 'vitest';
import { dayLabel, lineKey, PurchaseLine, sumAmount } from './purchasing';

const line = (p: Partial<PurchaseLine>): PurchaseLine => ({
  station_id: 's', station: 'АЗС', item_id: 1, item_key: 'k', name: 'Вода', group_name: null, mode: 'auto', abc: 'A', stock: 0, on_order: 0,
  per_day: 1, days_of_cover: null, in_stock_days: 30, zero_days: 0, low_data: false, safety: 0, target: 1, pack: 1, pack_learned: false,
  qty: 1, price: null, amount: null, status: 'out', min_qty: null, max_qty: null, note: null, last_sale: null, ...p,
});

describe('purchasing', () => {
  it('names the trip day', () => {
    expect(dayLabel('2026-11-23')).toBe('23.11, понедельник');
    expect(dayLabel('2026-11-22T00:00:00')).toBe('22.11, воскресенье');
  });
  it('counts the budget with edited quantities and skips lines without a price', () => {
    const a = line({ item_id: 1, qty: 12, price: 30 });
    const b = line({ item_id: 2, qty: 6, price: null });
    const c = line({ item_id: 3, qty: 2, price: 10.5 });
    expect(sumAmount([a, b, c], {})).toBe(381);
    expect(sumAmount([a, b, c], { [lineKey(a)]: 24 })).toBe(741);
  });
});
