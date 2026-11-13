import { InfoCircleOutlined } from '@ant-design/icons';
import { Card, Col, Row, Table, Tag, Tooltip, Typography } from 'antd';
import { useFilter, useFiltered } from '../api/client';
import { FilterBar } from '../components/FilterBar';
import { CHART_COLORS, ChartCard, Delta, FigureList, numCol, PageTitle, QueryState } from '../components/ui';
import { PayBreakdown } from '../components/PayBreakdown';
import { bucketLabel, compact, int, liters, money, Num, pct } from '../lib/format';
import { AttentionPanel } from '../components/AttentionPanel';
import { section } from '../nav';

export interface Kpi {
  fuel_revenue: number; liters: number; orders: number; aborted: number; service_liters: number; cashless: number; mixed: number;
  discounts: number; shop_revenue: number; shop_receipts: number; shop_items: number; revenue: number; avg_fill: number | null;
  avg_fuel_check: number | null; avg_shop_check: number | null; cashless_share: number | null; aborted_share: number | null;
  margin: number | null; fuel_coverage: number | null;
  cash: number; card: number; contract: number; fuel_card: number;
  card_share: number | null; contract_share: number | null; fuel_card_share: number | null;
  shop_cash: number; shop_card: number; shop_contract: number; shop_fuel_card: number;
  fuel_margin: number | null; shop_margin: number | null;
}

export interface StationRow {
  station_id: string; station: string; region: string | null; fuel_revenue: number; liters: number; orders: number; aborted: number;
  cashless: number; discounts: number; shop_revenue: number; shop_receipts: number; revenue: number; liters_per_day: number;
  avg_fill: number | null; avg_fuel_check: number | null; avg_shop_check: number | null; cashless_share: number | null; shop_to_fuel: number | null;
  cash: number; card: number; contract: number; fuel_card: number;
  card_share: number | null; contract_share: number | null; fuel_card_share: number | null;
}

interface Overview {
  filter: { g: string; days: number; compare_from: string | null; compare_to: string | null };
  current: Kpi;
  previous: Kpi | null;
  series: { bucket: string; fuel_revenue: number; liters: number; orders: number; shop_revenue: number; shop_receipts: number }[];
  by_fuel: { fuel: string; liters: number; revenue: number }[];
  stations: StationRow[];
  problems: { station_id: string; station: string; last_seen_at: string | null; silent_seconds: number | null; agent_queue_dead: number | null; topaz_db_ok: boolean | null }[];
}

/** "Итог за период": all the money (fuel + shop) by payment method, then fuel and shop each with their own figures. */
function PeriodSummary({ c, p, filtered }: { c: Kpi; p: Kpi | undefined; filtered: boolean }) {
  const d = (k: keyof Kpi, fmt: (v: Num) => string, inverse?: boolean) => <Delta value={c[k]} previous={p ? p[k] : undefined} format={fmt} inverse={inverse} />;
  const v = (k: keyof Kpi, fmt: (v: Num) => string, inverse?: boolean) => <>{fmt(c[k])}{d(k, fmt, inverse)}</>;
  const coverage = c.fuel_coverage != null && c.fuel_coverage < 0.999 ? `Себестоимость известна для ${pct(c.fuel_coverage)} проданных литров; остальные в маржу не входят.` : 'По продажам с известной себестоимостью.';
  const noShop = Number(c.shop_receipts) === 0;
  return (
    <>
      <Card size="small" style={{ marginBottom: 12 }}>
        <Row gutter={[24, 12]} align="middle">
          <Col xs={24} md={9}>
            <Typography.Text type="secondary">Выручка за период, всего</Typography.Text>
            <div style={{ fontSize: 28, fontWeight: 600, fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap' }}>
              {money(c.revenue)}{d('revenue', money)}
            </div>
            <Typography.Text type="secondary">топливо {money(c.fuel_revenue)} · магазин {money(c.shop_revenue)}</Typography.Text>
            {c.margin != null && (
              <div style={{ marginTop: 8 }}>
                <Typography.Text type="secondary">Валовая маржа <Tooltip title={coverage}><InfoCircleOutlined /></Tooltip></Typography.Text>
                <div style={{ fontSize: 18, fontWeight: 600, fontVariantNumeric: 'tabular-nums' }}>{money(c.margin)}{d('margin', money)}</div>
              </div>
            )}
            {filtered && <Typography.Paragraph type="secondary" style={{ fontSize: 12, margin: '8px 0 0' }}>С фильтром по топливу или способу оплаты магазин не учитывается.</Typography.Paragraph>}
          </Col>
          <Col xs={24} md={15}>
            <PayBreakdown exact={false}
              fuel={{ cash: c.cash, card: c.card, contract: c.contract, fuel_card: c.fuel_card }}
              shop={{ cash: c.shop_cash, card: c.shop_card, contract: c.shop_contract, fuel_card: c.shop_fuel_card }} />
          </Col>
        </Row>
      </Card>
      <Row gutter={[12, 12]} style={{ marginBottom: 12 }}>
        <Col xs={24} lg={12}>
          <Card size="small" title="Топливо" style={{ height: '100%' }}>
            <FigureList rows={[
              { key: 'l', label: 'Продано', value: v('liters', liters), hint: 'Без тех. прокачек и переливов' },
              { key: 'r', label: 'Выручка', value: v('fuel_revenue', money), strong: true },
              ...(c.fuel_margin != null ? [{ key: 'm', label: 'Валовая маржа', value: v('fuel_margin', money), hint: coverage }] : []),
              { key: 'o', label: 'Заказов', value: v('orders', int), hint: 'Наливы с объёмом больше нуля, без тех. прокачек' },
              { key: 'f', label: 'Средний налив', value: v('avg_fill', liters) },
              { key: 'c', label: 'Средний чек', value: v('avg_fuel_check', money) },
              { key: 'cl', label: 'Доля безнала', value: v('cashless_share', pct), hint: `Карты ${pct(c.card_share)}, договоры ${pct(c.contract_share)}, топливные карты ${pct(c.fuel_card_share)} — от выручки топлива` },
              { key: 'd', label: 'Скидки клиентам', value: v('discounts', money, true), hint: 'Уже вычтены из выручки' },
              { key: 'a', label: 'Прерванные наливы', value: v('aborted', int, true), hint: 'Заказы с нулевым объёмом' },
              { key: 's', label: 'Тех. прокачки', value: v('service_liters', liters, true), hint: 'Отпуск без оплаты: тех. прокачки, переливы' },
            ]} />
          </Card>
        </Col>
        <Col xs={24} lg={12}>
          <Card size="small" title="Магазин" style={{ height: '100%' }}>
            {noShop ? <Typography.Text type="secondary">{filtered ? 'С фильтром по топливу или способу оплаты магазин не показывается' : 'Продаж в магазине за период не было'}</Typography.Text> : (
              <FigureList rows={[
                { key: 'r', label: 'Выручка', value: v('shop_revenue', money), strong: true, hint: 'За вычетом возвратов' },
                ...(c.shop_margin != null ? [{ key: 'm', label: 'Валовая маржа', value: v('shop_margin', money), hint: 'По товарам с известной себестоимостью' }] : []),
                { key: 'n', label: 'Чеков', value: v('shop_receipts', int) },
                { key: 'c', label: 'Средний чек', value: v('avg_shop_check', money) },
                { key: 'i', label: 'Продано товаров, шт.', value: v('shop_items', int) },
              ]} />
            )}
          </Card>
        </Col>
      </Row>
    </>
  );
}

export default function OverviewPage() {
  const { data, error, isLoading, isFetching } = useFiltered<Overview>('/analytics/overview');
  const c = data?.current;
  const p = data?.previous ?? undefined;
  const g = data?.filter.g ?? 'day';
  const [filter] = useFilter();

  const series = data?.series ?? [];
  return (
    <>
      <PageTitle title={section('/').label} description={section('/').description} />
      <FilterBar fuel pay />
      <QueryState error={error} loading={isLoading}>
        {data && (
          <>
            <AttentionPanel problems={data.problems} />
            <PeriodSummary c={c!} p={p} filtered={!!(filter.fuel || filter.pay)} />
            <Row gutter={[12, 12]} style={{ marginTop: 12 }}>
              <Col xs={24} xl={16}>
                <ChartCard
                  title="Выручка и литры"
                  loading={isFetching}
                  empty={series.length === 0}
                  option={{
                    color: CHART_COLORS,
                    tooltip: { trigger: 'axis', valueFormatter: (v) => compact(Number(v)) },
                    legend: { top: 0 },
                    grid: { left: 8, right: 8, bottom: 8, top: 36, containLabel: true },
                    xAxis: { type: 'category', data: series.map((s) => bucketLabel(s.bucket, g)) },
                    yAxis: [
                      { type: 'value', axisLabel: { formatter: (v: number) => compact(v) } },
                      { type: 'value', axisLabel: { formatter: (v: number) => `${compact(v)} л` }, splitLine: { show: false } },
                    ],
                    series: [
                      { name: 'Топливо, ₽', type: 'bar', stack: 'r', data: series.map((s) => s.fuel_revenue) },
                      { name: 'Магазин, ₽', type: 'bar', stack: 'r', data: series.map((s) => s.shop_revenue) },
                      { name: 'Литры', type: 'line', yAxisIndex: 1, smooth: true, symbol: 'none', data: series.map((s) => s.liters) },
                    ],
                  }}
                />
              </Col>
              <Col xs={24} xl={8}>
                <ChartCard
                  title="Структура продаж топлива"
                  empty={data.by_fuel.length === 0}
                  option={{
                    color: CHART_COLORS,
                    tooltip: { trigger: 'item', formatter: (x: any) => `${x.name}<br/>${money(x.value)} (${x.percent}%)<br/>${liters(data.by_fuel[x.dataIndex].liters)}` },
                    legend: { bottom: 0 },
                    series: [{
                      type: 'pie', radius: ['40%', '62%'], center: ['50%', '46%'], avoidLabelOverlap: true, minShowLabelAngle: 4,
                      itemStyle: { borderRadius: 4 }, label: { formatter: '{b}\n{d}%', overflow: 'none' },
                      data: data.by_fuel.map((f) => ({ name: f.fuel, value: f.revenue })),
                    }],
                  }}
                />
              </Col>
            </Row>
            <Card size="small" title="АЗС за период" style={{ marginTop: 12 }}>
              <Table<StationRow>
                size="small"
                rowKey="station_id"
                pagination={false}
                bordered
                scroll={{ x: 'max-content' }}
                dataSource={data.stations}
                columns={[
                  {
                    title: 'АЗС', dataIndex: 'station', key: 'station', fixed: 'left', width: 220,
                    render: (v: string, r) => <span style={{ whiteSpace: 'nowrap' }}>{v}{r.region && <Typography.Text type="secondary"> · {r.region}</Typography.Text>}</span>,
                  },
                  { title: 'Всего', key: 'all', children: [numCol<StationRow>('Выручка', 'revenue', money, { defaultSortOrder: 'descend' })] },
                  {
                    title: 'Топливо', key: 'fuel', children: [
                      numCol<StationRow>('Выручка', 'fuel_revenue', money),
                      numCol<StationRow>('Литры', 'liters', liters),
                      numCol<StationRow>('В день', 'liters_per_day', liters),
                      numCol<StationRow>('Заказы', 'orders', int),
                      numCol<StationRow>('Ср. налив', 'avg_fill', liters),
                      numCol<StationRow>('Ср. чек', 'avg_fuel_check', money),
                      { ...numCol<StationRow>('Безнал', 'cashless_share', pct), title: <Tooltip title="Доля карт, договоров и топливных карт в выручке топлива">Безнал</Tooltip> },
                      { ...numCol<StationRow>('Прерв.', 'aborted', int), render: (v: number) => (v > 0 ? <Tag color="orange">{int(v)}</Tag> : '0') },
                    ],
                  },
                  {
                    title: 'Магазин', key: 'shop', children: [
                      numCol<StationRow>('Выручка', 'shop_revenue', money),
                      numCol<StationRow>('Ср. чек', 'avg_shop_check', money),
                    ],
                  },
                ]}
              />
            </Card>
            {data.previous && data.filter.compare_from && (
              <Typography.Paragraph type="secondary" style={{ marginTop: 8 }}>
                Сравнение с периодом {data.filter.compare_from.split('-').reverse().join('.')} – {data.filter.compare_to!.split('-').reverse().join('.')}.
              </Typography.Paragraph>
            )}
          </>
        )}
      </QueryState>
    </>
  );
}
