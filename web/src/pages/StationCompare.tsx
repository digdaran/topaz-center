import { Card, Segmented, Table } from 'antd';
import { useState } from 'react';
import { useFiltered } from '../api/client';
import { FilterBar } from '../components/FilterBar';
import { CHART_COLORS, ChartCard, numCol, QueryState } from '../components/ui';
import { int, liters, money, Num, pct } from '../lib/format';
import type { StationRow } from './OverviewPage';

const METRICS: { key: keyof StationRow; label: string; fmt: (v: Num) => string }[] = [
  { key: 'revenue', label: 'Выручка', fmt: money },
  { key: 'liters_per_day', label: 'Литров в день', fmt: liters },
  { key: 'avg_fuel_check', label: 'Ср. чек топливо', fmt: money },
  { key: 'avg_fill', label: 'Средний налив', fmt: liters },
  { key: 'cashless_share', label: 'Доля безнала', fmt: pct },
  { key: 'card_share', label: 'Доля карт', fmt: pct },
  { key: 'contract_share', label: 'Доля договоров', fmt: pct },
  { key: 'shop_to_fuel', label: 'Магазин / топливо', fmt: pct },
  { key: 'avg_shop_check', label: 'Ср. чек магазин', fmt: money },
];

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
          title={<Segmented size="small" value={metric} onChange={(v) => setMetric(v as keyof StationRow)} options={METRICS.map((x) => ({ value: x.key, label: x.label }))} />}
          height={Math.max(220, rows.length * 34 + 40)}
          empty={!rows.length}
          option={{
            color: [CHART_COLORS[0]],
            tooltip: { trigger: 'axis', valueFormatter: (v: any) => m.fmt(v) },
            grid: { left: 8, right: 48, top: 8, bottom: 8, containLabel: true },
            xAxis: { type: 'value', axisLabel: { formatter: (v: number) => (m.fmt === pct ? `${Math.round(v * 100)}%` : String(v)) } },
            yAxis: { type: 'category', inverse: true, data: rows.map((r) => r.station) },
            series: [{ type: 'bar', data: rows.map((r) => r[metric] ?? 0), label: { show: true, position: 'right', formatter: (p: any) => m.fmt(p.value) } }],
          }}
        />
        <Card size="small" style={{ marginTop: 12 }}>
          <Table<StationRow> size="small" rowKey="station_id" pagination={false} dataSource={rows} scroll={{ x: 1100 }}
            columns={[
              { title: 'АЗС', dataIndex: 'station', fixed: 'left', width: 200 },
              ...METRICS.map((x) => numCol<StationRow>(x.label, x.key as any, x.fmt)),
              numCol<StationRow>('Заказы', 'orders', int),
              numCol<StationRow>('Скидки', 'discounts', money),
            ]} />
        </Card>
      </QueryState>
    </>
  );
}
