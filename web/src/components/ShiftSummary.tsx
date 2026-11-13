import { InfoCircleOutlined } from '@ant-design/icons';
import { Alert, Card, Col, Flex, Row, Tag, theme, Tooltip, Typography } from 'antd';
import { Figure, FigureList } from './ui';
import { int, liters, money, Num, toNum } from '../lib/format';
import { PAY } from '../lib/payments';

/** Shift header figures (ShiftEndpoints.Card): fuel and shop by payment method, the till (Topaz rgCashes). */
export interface ShiftMoney {
  ended_at: string | null;
  fuel_volume: Num; fuel_amount: Num; hose_counters_delta: Num;
  pay_cash: Num; pay_card: Num; pay_contract: Num; pay_fuel_card: Num;
  orders: Num; aborted: Num; discounts: Num;
  shop_total: Num; shop_receipts: Num; shop_cash: Num; shop_card: Num; shop_contract: Num; shop_fuel_card: Num;
  cash_in: Num; cash_out: Num; cash_fuel: Num; cash_shop: Num; cash_other: Num; cash_end: Num; cash_moves: Num;
}

const n = (v: Num) => toNum(v) ?? 0;
const rub = (v: Num) => money(v, true);
/** "+ 500,00 ₽" / "− 1 600,00 ₽": the sign apart, so the column reads as a sum. */
const signed = (v: number) => `${v < 0 ? '−' : '+'} ${money(Math.abs(v), true)}`;

const METHODS = [
  { key: 'cash', label: 'Наличные', fuel: 'pay_cash', shop: 'shop_cash' },
  { key: 'card', label: 'Банковские карты', fuel: 'pay_card', shop: 'shop_card' },
  { key: 'contract', label: 'По договорам', fuel: 'pay_contract', shop: 'shop_contract' },
  { key: 'fuel_card', label: 'Топливные карты', fuel: 'pay_fuel_card', shop: 'shop_fuel_card' },
] as const;

function usePayColor() {
  const { token } = theme.useToken();
  const palette = token as unknown as Record<string, string>;
  return (key: keyof typeof PAY) => palette[PAY[key].color] ?? token.colorTextSecondary;
}

/** Fuel or shop by payment method: only the methods that were used. */
function methodRows(h: ShiftMoney, side: 'fuel' | 'shop', color: (k: keyof typeof PAY) => string): Figure[] {
  return METHODS.filter((m) => n(h[m[side]]) !== 0).map((m) => ({ key: m.key, label: m.label, value: rub(h[m[side]]), dot: color(m.key), sub: true }));
}

/** "Итог смены": all the money of the shift, fuel + shop, by payment method with a proportion bar. */
function Total({ h }: { h: ShiftMoney }) {
  const color = usePayColor();
  const fuel = n(h.fuel_amount), shop = n(h.shop_total), total = fuel + shop;
  const methods = METHODS.map((m) => ({ ...m, fuelV: n(h[m.fuel]), shopV: n(h[m.shop]) }))
    .map((m) => ({ ...m, v: m.fuelV + m.shopV })).filter((m) => m.v !== 0);
  const paid = methods.reduce((a, m) => a + Math.max(m.v, 0), 0);
  return (
    <Card size="small" style={{ marginBottom: 12 }}>
      <Row gutter={[24, 12]} align="middle">
        <Col xs={24} md={9}>
          <Typography.Text type="secondary">Выручка за смену, всего</Typography.Text>
          <div style={{ fontSize: 28, fontWeight: 600, fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap' }}>{rub(total)}</div>
          <Typography.Text type="secondary">топливо {rub(fuel)} · магазин {rub(shop)}</Typography.Text>
        </Col>
        <Col xs={24} md={15}>
          {paid > 0 && (
            <Flex style={{ height: 10, borderRadius: 5, overflow: 'hidden', marginBottom: 8 }}>
              {methods.filter((m) => m.v > 0).map((m) => (
                <Tooltip key={m.key} title={`${m.label}: ${rub(m.v)}`}>
                  <div style={{ width: `${(m.v / paid) * 100}%`, background: color(m.key) }} />
                </Tooltip>
              ))}
            </Flex>
          )}
          <FigureList rows={methods.map((m) => ({
            key: m.key, label: m.label, dot: color(m.key), value: rub(m.v),
            hint: `топливо ${rub(m.fuelV)} + магазин ${rub(m.shopV)}`,
          }))} />
        </Col>
      </Row>
    </Card>
  );
}

/** Pump counters against the litres of the orders: the same check as the shift list's red tag. */
function CounterCheck({ h }: { h: ShiftMoney }) {
  const counters = toNum(h.hose_counters_delta);
  if (counters === null) return <Typography.Text type="secondary">Счётчики ТРК за смену не пришли</Typography.Text>;
  const diff = counters - n(h.fuel_volume);
  return (
    <Flex justify="space-between" align="center" gap={8} wrap>
      <Typography.Text type="secondary">Счётчики ТРК: {liters(counters)}</Typography.Text>
      {Math.abs(diff) <= 0.5
        ? <Tag color="green">совпадает с отпуском</Tag>
        : <Tooltip title="Разница счётчиков ТРК за смену не равна сумме отпусков по заказам"><Tag color="red">расходится на {liters(Math.abs(diff))}</Tag></Tooltip>}
    </Flex>
  );
}

function Fuel({ h }: { h: ShiftMoney }) {
  const color = usePayColor();
  const rows: Figure[] = [
    { key: 'l', label: 'Отпущено', value: liters(h.fuel_volume) },
    { key: 'r', label: 'Выручка', value: rub(h.fuel_amount), strong: true },
    ...methodRows(h, 'fuel', color),
    { key: 'o', label: 'Заказов', value: int(h.orders) },
    ...(n(h.aborted) > 0 ? [{ key: 'a', label: 'Прерванных наливов', value: <Tag color="orange" style={{ marginInlineEnd: 0 }}>{int(h.aborted)}</Tag> }] : []),
    ...(n(h.discounts) > 0 ? [{ key: 'd', label: 'Скидки клиентам', value: rub(h.discounts), hint: 'Уже вычтены из выручки' }] : []),
  ];
  return (
    <Card size="small" title="Топливо" style={{ height: '100%' }}>
      <FigureList rows={rows} />
      <div style={{ marginTop: 10 }}><CounterCheck h={h} /></div>
    </Card>
  );
}

function Shop({ h }: { h: ShiftMoney }) {
  const color = usePayColor();
  const empty = n(h.shop_receipts) === 0;
  return (
    <Card size="small" title="Магазин" style={{ height: '100%' }}>
      {empty ? <Typography.Text type="secondary">Продаж в магазине не было</Typography.Text> : (
        <FigureList rows={[
          { key: 'r', label: 'Выручка', value: rub(h.shop_total), strong: true, hint: 'За вычетом возвратов' },
          ...methodRows(h, 'shop', color),
          { key: 'c', label: 'Чеков', value: int(h.shop_receipts) },
        ]} />
      )}
    </Card>
  );
}

/** "Наличные в кассе": the till as Topaz's money register counts it, written as a sum. */
function Till({ h }: { h: ShiftMoney }) {
  const open = !h.ended_at;
  const title = (
    <>Наличные в кассе{' '}
      <Tooltip title="Как считает касса Топаза: внесённый размен, наличная оплата топлива и товаров, выдача (инкассация). Безнал сюда не входит.">
        <InfoCircleOutlined style={{ fontWeight: 400 }} />
      </Tooltip>
    </>
  );
  if (n(h.cash_moves) === 0) {
    return <Card size="small" title={title} style={{ height: '100%' }}><Typography.Text type="secondary">В этой смене касса Топаза движений не записала</Typography.Text></Card>;
  }
  const rows: Figure[] = [
    { key: 'in', label: 'Внесено (размен)', value: signed(n(h.cash_in)) },
    { key: 'fuel', label: 'Наличные за топливо', value: signed(n(h.cash_fuel)) },
    { key: 'shop', label: 'Наличные за магазин', value: signed(n(h.cash_shop)) },
    { key: 'other', label: 'Прочие операции', value: signed(n(h.cash_other)) },
    { key: 'out', label: 'Выдано (инкассация)', value: signed(n(h.cash_out)) },
  ].filter((r) => r.value !== signed(0));
  // Sales counted as cash here but not in the till (or the other way round): a payment method is probably misclassified.
  const mismatch = Math.abs(n(h.cash_fuel) - n(h.pay_cash)) > 1 || Math.abs(n(h.cash_shop) - n(h.shop_cash)) > 1;
  return (
    <Card size="small" title={title} style={{ height: '100%' }}>
      <FigureList rows={rows} total={{ key: 'end', label: open ? 'Сейчас в кассе' : 'Остаток на конец смены', value: rub(h.cash_end) }} />
      {mismatch && (
        <Alert type="warning" showIcon style={{ marginTop: 10 }}
          message="Наличные по продажам не совпадают с кассой Топаза"
          description={<>По продажам наличными: топливо {rub(h.pay_cash)}, магазин {rub(h.shop_cash)}. Скорее всего, способ оплаты какого-то вида оплаты определён неверно — проверьте <b>Настройки → Виды оплаты</b>.</>} />
      )}
    </Card>
  );
}

/** The money of a shift: total by payment method, then fuel, shop and the till side by side. */
export function ShiftSummary({ h }: { h: ShiftMoney }) {
  return (
    <>
      <Total h={h} />
      <Row gutter={[12, 12]} style={{ marginBottom: 12 }}>
        <Col xs={24} md={12} xl={8}><Fuel h={h} /></Col>
        <Col xs={24} md={12} xl={8}><Shop h={h} /></Col>
        <Col xs={24} xl={8}><Till h={h} /></Col>
      </Row>
    </>
  );
}
