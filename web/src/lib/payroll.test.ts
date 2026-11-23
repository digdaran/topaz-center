import { describe, expect, it } from 'vitest';
import { monthLabel, schemeOn, schemeText, slipHtml } from './payroll';

describe('payroll', () => {
  it('describes a scheme in plain words', () => {
    expect(schemeText({ shift_fixed: 3000, fuel_pct: 0, shop_pct: 10, per_liter: 0 }).replace(/\s/g, ' ')).toBe('3 000 ₽ за смену + 10 % магазина');
    expect(schemeText({ shift_fixed: 0, fuel_pct: 2, shop_pct: 0, per_liter: 0.5 })).toBe('2 % топлива + 0,5 ₽ за литр');
    expect(schemeText({ shift_fixed: 0, fuel_pct: 0, shop_pct: 0, per_liter: 0 })).toBe('ничего не начисляется');
  });

  it('finds the scheme in force on a date', () => {
    const s = [{ id: 1, valid_from: '2026-01-01' }, { id: 2, valid_from: '2026-03-01' }];
    expect(schemeOn(s, '2025-12-31')).toBeUndefined();
    expect(schemeOn(s, '2026-02-28')?.id).toBe(1);
    expect(schemeOn(s, '2026-03-01')?.id).toBe(2);
  });

  it('names the month', () => {
    expect(monthLabel('2026-11')).toBe('ноябрь 2026');
  });

  it('prints a payslip with names escaped', () => {
    const html = slipHtml({
      login: '<Инна>', station: 'АЗС & Ко', month: '2026-10', closed_at: null,
      employee: { shifts: 1, accrued: 3020, bonus: 0, fine: 0, total: 3020, advance: 1000, to_pay: 2020 },
      shifts: [{ local_date: '2026-10-02', session_num: 7, hours: 24, fuel_revenue: 1000, liters: 20, shop_revenue: 0, fixed: 3000, fuel_pay: 20, shop_pay: 0, liter_pay: 0, total: 3020, fixed_share: 1 }],
      adjustments: [{ on_date: '2026-10-15', kind: 'advance', amount: 1000, comment: null }],
      schemes: [{ valid_from: '2026-10-01', shift_fixed: 3000, fuel_pct: 2, shop_pct: 0, per_liter: 0 }],
    });
    expect(html).toContain('&lt;Инна&gt;');
    expect(html).toContain('АЗС &amp; Ко');
    expect(html).toContain('02.10.2026');
    expect(html).toContain('Аванс');
    expect(html).toContain('предварительно');
    expect(html).not.toContain('<Инна>');
  });
});
