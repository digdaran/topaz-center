/** Operators' pay (1.33, Деньги → Зарплаты): labels, scheme text and the printable payslip. */
import { money } from './format';

export type AdjustmentKind = 'bonus' | 'fine' | 'advance';

export const ADJUSTMENT_KINDS: { value: AdjustmentKind; label: string }[] = [
  { value: 'bonus', label: 'Премия' },
  { value: 'fine', label: 'Штраф' },
  { value: 'advance', label: 'Аванс' },
];

export const adjustmentLabel = (kind: string) => ADJUSTMENT_KINDS.find((k) => k.value === kind)?.label ?? kind;

export interface PayScheme {
  id: number;
  login?: string | null;
  valid_from: string;
  shift_fixed: number;
  fuel_pct: number;
  shop_pct: number;
  per_liter: number;
  min_shift_hours: number;
  excluded_groups: number[];
}

const n = (v: number | string | null | undefined) => Number(v ?? 0);
const num = (v: number) => new Intl.NumberFormat('ru-RU', { maximumFractionDigits: 3 }).format(v);

/** "3 000 ₽ за смену + 2 % топлива + 10 % магазина + 0,5 ₽ за литр". */
export function schemeText(s: Pick<PayScheme, 'shift_fixed' | 'fuel_pct' | 'shop_pct' | 'per_liter'>): string {
  const parts: string[] = [];
  if (n(s.shift_fixed) > 0) parts.push(`${num(n(s.shift_fixed))} ₽ за смену`);
  if (n(s.fuel_pct) > 0) parts.push(`${num(n(s.fuel_pct))} % топлива`);
  if (n(s.shop_pct) > 0) parts.push(`${num(n(s.shop_pct))} % магазина`);
  if (n(s.per_liter) > 0) parts.push(`${num(n(s.per_liter))} ₽ за литр`);
  return parts.length ? parts.join(' + ') : 'ничего не начисляется';
}

/** The scheme in force on a date (yyyy-MM-dd) among one owner's schemes. */
export function schemeOn<T extends { valid_from: string }>(schemes: T[], date: string): T | undefined {
  return schemes.filter((s) => s.valid_from.slice(0, 10) <= date).sort((a, b) => b.valid_from.localeCompare(a.valid_from))[0];
}

/** "2026-11" → "ноябрь 2026". */
export function monthLabel(month: string): string {
  const [y, m] = month.split('-').map(Number);
  return `${new Date(y, m - 1, 1).toLocaleString('ru-RU', { month: 'long' })} ${y}`;
}

const esc = (s: unknown) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]!);
const day = (iso: string) => iso.slice(0, 10).split('-').reverse().join('.');

type R = Record<string, any>;

/** A payslip as a standalone HTML page for printing (opened in a new window). */
export function slipHtml(slip: R): string {
  const e = slip.employee as R;
  const shifts = (slip.shifts as R[]).map((s) => `<tr${s.excluded ? ' class="off"' : ''}>
      <td>${day(s.local_date)}</td><td>${esc(s.session_num ?? s.session_id)}</td><td class="r">${esc(s.hours)}</td>
      <td class="r">${money(s.fuel_revenue, true)}</td><td class="r">${esc(s.liters)}</td><td class="r">${money(s.shop_revenue, true)}</td>
      <td class="r">${money(s.fixed, true)}</td><td class="r">${money(n(s.fuel_pay) + n(s.shop_pay) + n(s.liter_pay), true)}</td>
      <td class="r"><b>${money(s.total, true)}</b></td>
      <td>${[s.excluded ? 'не оплачивается' : '', s.short ? 'короткая' : '', s.open ? 'идёт' : '', n(s.fixed_share) > 0 && n(s.fixed_share) < 1 ? `фикс ${Math.round(n(s.fixed_share) * 100)} %` : '', s.alerts ? `событий: ${s.alerts}` : ''].filter(Boolean).join(', ')}</td>
    </tr>`).join('');
  const adjustments = (slip.adjustments as R[]).map((a) => `<tr><td>${day(a.on_date)}</td><td>${esc(adjustmentLabel(a.kind))}</td><td>${esc(a.comment)}</td>
      <td class="r">${a.kind === 'bonus' ? '' : '−'}${money(a.amount, true)}</td></tr>`).join('');
  const schemes = (slip.schemes as R[]).map((s) => `<li>с ${day(s.valid_from)}: ${esc(schemeText(s as PayScheme))}</li>`).join('');
  return `<!doctype html><html lang="ru"><head><meta charset="utf-8"><title>Расчётный листок — ${esc(slip.login)}</title>
<style>
  body { font: 13px/1.4 system-ui, sans-serif; margin: 24px; color: #000; }
  h1 { font-size: 18px; margin: 0 0 4px; } .sub { color: #555; margin-bottom: 16px; }
  table { border-collapse: collapse; width: 100%; margin: 8px 0 16px; } th, td { border: 1px solid #bbb; padding: 3px 6px; text-align: left; }
  th { background: #f0f0f0; } .r { text-align: right; white-space: nowrap; } tr.off td { color: #888; text-decoration: line-through; }
  .sum td { border: none; padding: 2px 6px; } .sum .r { font-variant-numeric: tabular-nums; } .total td { font-weight: 700; font-size: 15px; }
  .sign { margin-top: 32px; display: flex; gap: 48px; }
</style></head><body>
<h1>Расчётный листок: ${esc(slip.login)}</h1>
<div class="sub">${esc(slip.station)} · ${esc(monthLabel(slip.month))}${slip.closed_at ? ' · месяц закрыт' : ' · предварительно'}</div>
${schemes ? `<div>Схема оплаты:<ul>${schemes}</ul></div>` : ''}
<table><thead><tr><th>Дата</th><th>Смена</th><th class="r">Часов</th><th class="r">Топливо, ₽</th><th class="r">Литры</th><th class="r">Магазин, ₽</th>
<th class="r">Фикс</th><th class="r">Проценты и литры</th><th class="r">Итого</th><th>Отметки</th></tr></thead><tbody>${shifts}</tbody></table>
${adjustments ? `<table><thead><tr><th>Дата</th><th>Строка</th><th>Комментарий</th><th class="r">Сумма</th></tr></thead><tbody>${adjustments}</tbody></table>` : ''}
<table class="sum">
<tr><td>Смен</td><td class="r">${esc(e.shifts)}</td></tr>
<tr><td>Начислено по схеме</td><td class="r">${money(e.accrued, true)}</td></tr>
<tr><td>Премии</td><td class="r">${money(e.bonus, true)}</td></tr>
<tr><td>Штрафы</td><td class="r">−${money(e.fine, true)}</td></tr>
<tr><td>Итого начислено</td><td class="r">${money(e.total, true)}</td></tr>
<tr><td>Авансы</td><td class="r">−${money(e.advance, true)}</td></tr>
<tr class="total"><td>К выдаче</td><td class="r">${money(e.to_pay, true)}</td></tr>
</table>
<div class="sign"><span>Выдал: ____________</span><span>Получил: ____________</span></div>
</body></html>`;
}
