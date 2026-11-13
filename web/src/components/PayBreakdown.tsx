import { Flex, theme, Tooltip } from 'antd';
import { ReactNode } from 'react';
import { money, Num, toNum } from '../lib/format';
import { PAY, PayCategory } from '../lib/payments';
import { Figure, FigureList } from './ui';

/** The four ways customers pay, in the same order and colours everywhere (shift card, summary). */
export const PAY_METHODS: { key: PayCategory; label: string }[] = [
  { key: 'cash', label: 'Наличные' },
  { key: 'card', label: 'Банковские карты' },
  { key: 'contract', label: 'По договорам' },
  { key: 'fuel_card', label: 'Топливные карты' },
];

export function usePayColor() {
  const { token } = theme.useToken();
  const palette = token as unknown as Record<string, string>;
  return (key: PayCategory) => palette[PAY[key].color] ?? token.colorTextSecondary;
}

const n = (v: Num) => toNum(v) ?? 0;

/**
 * Money by payment method, fuel + shop together: a proportion bar and one line per method used
 * (the hint splits it into fuel and shop). `extra` adds lines after the methods (e.g. a change to the previous period).
 */
export function PayBreakdown({ fuel, shop, exact = true, extra }: {
  fuel: Partial<Record<PayCategory, Num>>;
  shop: Partial<Record<PayCategory, Num>>;
  exact?: boolean;
  extra?: (key: PayCategory) => ReactNode;
}) {
  const color = usePayColor();
  const rub = (v: Num) => money(v, exact);
  const rows = PAY_METHODS.map((m) => ({ ...m, f: n(fuel[m.key]), s: n(shop[m.key]) }))
    .map((m) => ({ ...m, v: m.f + m.s })).filter((m) => m.v !== 0);
  const paid = rows.reduce((a, m) => a + Math.max(m.v, 0), 0);
  return (
    <>
      {paid > 0 && (
        <Flex style={{ height: 10, borderRadius: 5, overflow: 'hidden', marginBottom: 8 }}>
          {rows.filter((m) => m.v > 0).map((m) => (
            <Tooltip key={m.key} title={`${m.label}: ${rub(m.v)} (${Math.round((m.v / paid) * 100)} %)`}>
              <div style={{ width: `${(m.v / paid) * 100}%`, background: color(m.key) }} />
            </Tooltip>
          ))}
        </Flex>
      )}
      <FigureList rows={rows.map((m): Figure => ({
        key: m.key, label: m.label, dot: color(m.key),
        value: <>{rub(m.v)}{paid > 0 && <span style={{ opacity: 0.55, marginLeft: 8, fontSize: 12 }}>{Math.round((Math.max(m.v, 0) / paid) * 100)} %</span>}{extra?.(m.key)}</>,
        hint: `топливо ${rub(m.f)} + магазин ${rub(m.s)}`,
      }))} />
    </>
  );
}
