import { Card, Select, Table, Tooltip } from 'antd';
import { useState } from 'react';
import { useFiltered } from '../api/client';
import { FilterBar } from '../components/FilterBar';
import { CHART_COLORS, ChartCard, numCol, QueryState } from '../components/ui';
import { compact, int, liters, money, Num, pct } from '../lib/format';
import type { StationRow } from './OverviewPage';

/** Scope in the label: «всего» = fuel + shop; the rest is fuel or shop only. */
const METRICS: { key: keyof StationRow; label: string; group: 'Всего' | 'Топливо' | 'Магазин'; fmt: (v: Num) => string; hint?: string }[] = [
  { key: 'revenue', label: 'Выручка', group: 'Всего', fmt: money, hint: 'Топливо + магазин' },
  { key: 'fuel_revenue', label: 'Выручка', group: 'Топливо', fmt: money },
  { key: 'liters_per_day', label: 'Литров в день', group: 'Топливо', fmt: liters },
  { key: 'avg_fuel_check', label: 'Средний чек', group: 'Топливо', fmt: money },
  { key: 'avg_fill', label: 'Средний налив', group: 'Топливо', fmt: liters },
  { key: 'cashless_share', label: 'Доля безнала', group: 'Топливо', fmt: pct, hint: 'Карты, договоры и топливные карты — от выручки топлива' },
  { key: 'card_share', label: 'Доля карт', group: 'Топливо', fmt: pct },
  { key: 'contract_share', label: 'Доля договоров', group: 'Топливо', fmt: pct },
  { key: 'shop_revenue', label: 'Выручка', group: 'Магазин', fmt: money },
  { key: 'avg_shop_check', label: 'Средний чек', group: 'Магазин', fmt: money },
  { key: 'shop_to_fuel', label: 'Магазин к топливу', group: 'Магазин', fmt: pct, hint: 'Выручка магазина в процентах от выручки топлива' },
];
const fullLabel = (x: (typeof METRICS)[number]) => (x.group === 'Всего' ? `${x.label}, всего` : `${x.group}: ${x.label.toLowerCase()}`);

/** Stations side by side on the key metrics for the chosen period. */
export default function StationCompare() {
  const { data, error, isLoading } = useFiltered<{ stations: StationRow[] }>('/analytics/stations');
  const [metric, setMetric] = useState<keyof StationRow>('revenue');
  const m = METRICS.find((x) => x.key === metric)!;
  const rows = [...(data?.stations ?? [])].sort((a, b) => Number(b[metric] ?? 0) - Number(a[metric] ?? 0));
  return (
    <>
      <FilterBar granularity={false} compare={false} fuel pay />
      <QueryState error={error} loading={isLoading}>
        <ChartCard
          title={fullLabel(m)}
          extra={<Select size="small" style={{ width: 240 }} value={metric} onChange={setMetric}
            options={(['Всего', 'Топливо', 'Магазин'] as const).map((g) => ({ label: g, options: METRICS.filter((x) => x.group === g).map((x) => ({ value: x.key, label: fullLabel(x) })) }))} />}
          height={Math.max(220, rows.length * 34 + 40)}
          empty={!rows.length}
          option={{
            color: [CHART_COLORS[0]],
            tooltip: { trigger: 'axis', valueFormatter: (v: any) => m.fmt(v) },
            grid: { left: 8, right: 48, top: 8, bottom: 8, containLabel: true },
            xAxis: { type: 'value', axisLabel: { formatter: (v: number) => (m.fmt === pct ? `${Math.round(v * 100)} %` : compact(v)) } },
            yAxis: { type: 'category', inverse: true, data: rows.map((r) => r.station) },
            series: [{ type: 'bar', data: rows.map((r) => r[metric] ?? 0), label: { show: true, position: 'right', formatter: (p: any) => m.fmt(p.value) } }],
          }}
        />
        <Card size="small" style={{ marginTop: 12 }}>
          <Table<StationRow> size="small" rowKey="station_id" pagination={false} dataSource={rows} bordered scroll={{ x: 'max-content' }}
            columns={[
              { title: 'АЗС', dataIndex: 'station', fixed: 'left' },
              ...(['Всего', 'Топливо', 'Магазин'] as const).map((g) => ({
                title: g, key: g,
                children: [
                  ...METRICS.filter((x) => x.group === g).map((x) => ({
                    ...numCol<StationRow>(x.label, x.key as any, x.fmt),
                    title: x.hint ? <Tooltip title={x.hint}>{x.label}</Tooltip> : x.label,
                    className: x.key === metric ? 'ant-table-column-sort' : undefined,
                  })),
                  ...(g === 'Топливо' ? [numCol<StationRow>('Заказы', 'orders', int), { ...numCol<StationRow>('Скидки', 'discounts', money), title: <Tooltip title="Скидки клиентам на топливо">Скидки</Tooltip> }] : []),
                ],
              })),
            ]} />
        </Card>
      </QueryState>
    </>
  );
}
