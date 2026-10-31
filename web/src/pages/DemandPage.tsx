import { Card, Col, Row, Segmented, Table, Typography } from 'antd';
import { useState } from 'react';
import { useFiltered } from '../api/client';
import { FilterBar } from '../components/FilterBar';
import { CHART_COLORS, ChartCard, numCol, PageTitle, QueryState } from '../components/ui';
import { bucketLabel, compact, int, liters, money, MONTHS, WEEKDAYS } from '../lib/format';

type R = Record<string, any>;
interface Demand {
  fuel_heatmap: { dow: number; hour: number; orders: number; liters: number; revenue: number }[];
  shop_heatmap: { dow: number; hour: number; receipts: number; revenue: number }[];
  trend: R[];
  forecast: { day: string; liters: number; revenue: number }[];
  months: R[];
}

const HOURS = Array.from({ length: 24 }, (_, h) => `${String(h).padStart(2, '0')}:00`);
type Metric = 'orders' | 'liters' | 'revenue';
const METRIC: Record<Metric, { label: string; fmt: (v: number) => string }> = {
  orders: { label: 'Заказы', fmt: (v) => int(v) },
  liters: { label: 'Литры', fmt: (v) => liters(v) },
  revenue: { label: 'Выручка', fmt: (v) => money(v) },
};

function heatmap(points: { dow: number; hour: number; value: number }[], fmt: (v: number) => string, title: string) {
  const max = Math.max(1, ...points.map((p) => p.value));
  return {
    tooltip: { formatter: (p: any) => `${WEEKDAYS[p.value[1]]}, ${HOURS[p.value[0]]}<br/>${title}: ${fmt(p.value[2])} в среднем за день` },
    grid: { left: 8, right: 8, top: 8, bottom: 56, containLabel: true },
    xAxis: { type: 'category' as const, data: HOURS, splitArea: { show: true } },
    yAxis: { type: 'category' as const, data: WEEKDAYS, inverse: true, splitArea: { show: true } },
    visualMap: { min: 0, max, calculable: true, orient: 'horizontal' as const, left: 'center', bottom: 0, inRange: { color: ['#f0f5ff', '#69b1ff', '#0958d9', '#002c8c'] }, formatter: (v: any) => compact(Number(v)) },
    series: [{ type: 'heatmap' as const, data: points.map((p) => [p.hour, p.dow - 1, p.value]), emphasis: { itemStyle: { borderColor: '#000', borderWidth: 1 } } }],
  };
}

export default function DemandPage() {
  const { data, error, isLoading, isFetching } = useFiltered<Demand>('/analytics/demand');
  const [metric, setMetric] = useState<Metric>('liters');
  const m = METRIC[metric];

  const byHour = HOURS.map((_, h) => (data?.fuel_heatmap ?? []).filter((p) => p.hour === h).reduce((a, p) => a + p[metric], 0) / 7);
  const byDow = WEEKDAYS.map((_, d) => (data?.fuel_heatmap ?? []).filter((p) => p.dow === d + 1).reduce((a, p) => a + p[metric], 0));
  const trend = data?.trend ?? [];
  const fc = data?.forecast ?? [];

  return (
    <>
      <PageTitle title="Спрос" extra={
        <Segmented<Metric> value={metric} onChange={setMetric} options={(Object.keys(METRIC) as Metric[]).map((k) => ({ value: k, label: METRIC[k].label }))} />
      } />
      <FilterBar granularity={false} compare={false} fuel />
      <QueryState error={error} loading={isLoading}>
        {data && (
          <Row gutter={[12, 12]}>
            <Col xs={24}>
              <ChartCard title={`Топливо: ${m.label.toLowerCase()} по дням недели и часам (в среднем за день)`} height={340} loading={isFetching}
                empty={!data.fuel_heatmap.length}
                option={heatmap(data.fuel_heatmap.map((p) => ({ dow: p.dow, hour: p.hour, value: p[metric] })), m.fmt, m.label)} />
            </Col>
            <Col xs={24} lg={12}>
              <ChartCard title={`По часам суток (в среднем за день): ${m.label.toLowerCase()}`} empty={!data.fuel_heatmap.length} option={{
                color: [CHART_COLORS[0]],
                tooltip: { trigger: 'axis', valueFormatter: (v: any) => m.fmt(Number(v)) },
                grid: { left: 8, right: 8, bottom: 8, top: 16, containLabel: true },
                xAxis: { type: 'category', data: HOURS },
                yAxis: { type: 'value', axisLabel: { formatter: (v: number) => compact(v) } },
                series: [{ type: 'bar', data: byHour.map((v) => Math.round(v * 100) / 100) }],
              }} />
            </Col>
            <Col xs={24} lg={12}>
              <ChartCard title={`По дням недели (в среднем за день): ${m.label.toLowerCase()}`} empty={!data.fuel_heatmap.length} option={{
                color: [CHART_COLORS[1]],
                tooltip: { trigger: 'axis', valueFormatter: (v: any) => m.fmt(Number(v)) },
                grid: { left: 8, right: 8, bottom: 8, top: 16, containLabel: true },
                xAxis: { type: 'category', data: WEEKDAYS },
                yAxis: { type: 'value', axisLabel: { formatter: (v: number) => compact(v) } },
                series: [{ type: 'bar', data: byDow.map((v) => Math.round(v * 100) / 100) }],
              }} />
            </Col>
            <Col xs={24}>
              <ChartCard title={<>Тренд и прогноз на 14 дней <Typography.Text type="secondary" style={{ fontWeight: 400 }}>· прогноз — оценка по тем же дням недели с учётом тренда</Typography.Text></>}
                empty={!trend.length} option={{
                  color: [CHART_COLORS[0], CHART_COLORS[2], CHART_COLORS[3]],
                  tooltip: { trigger: 'axis', valueFormatter: (v: any) => (v == null ? '—' : metric === 'revenue' ? money(Number(v)) : liters(Number(v))) },
                  legend: { top: 0 },
                  grid: { left: 8, right: 8, bottom: 8, top: 36, containLabel: true },
                  xAxis: { type: 'category', data: [...trend.map((t) => t.day), ...fc.map((f) => f.day)].map((d) => bucketLabel(d, 'day')) },
                  yAxis: { type: 'value', axisLabel: { formatter: (v: number) => compact(v) } },
                  series: [
                    { name: metric === 'revenue' ? 'Выручка' : 'Литры', type: 'bar', data: trend.map((t) => (metric === 'revenue' ? t.revenue : t.liters)) },
                    { name: 'Среднее за 7 дней', type: 'line', smooth: true, symbol: 'none', data: trend.map((t) => (metric === 'revenue' ? t.revenue_ma7 : t.liters_ma7)) },
                    { name: 'Прогноз', type: 'line', symbol: 'circle', lineStyle: { type: 'dashed' }, data: [...trend.map(() => null), ...fc.map((f) => (metric === 'revenue' ? f.revenue : f.liters))] },
                  ],
                }} />
            </Col>
            <Col xs={24} lg={12}>
              <ChartCard title="Магазин: чеки по дням недели и часам (в среднем за день)" height={300} empty={!data.shop_heatmap.length}
                option={heatmap(data.shop_heatmap.map((p) => ({ dow: p.dow, hour: p.hour, value: p.receipts })), (v) => int(v), 'Чеки')} />
            </Col>
            <Col xs={24} lg={12}>
              <Card size="small" title="По месяцам" style={{ height: '100%' }}>
                <Table size="small" rowKey="month" pagination={false} dataSource={data.months}
                  columns={[
                    { title: 'Месяц', dataIndex: 'month', render: (v: string) => `${MONTHS[Number(v.slice(5, 7)) - 1]} ${v.slice(0, 4)}` },
                    numCol<R>('Литры', 'liters', liters),
                    numCol<R>('Выручка', 'revenue', money),
                    numCol<R>('Дней', 'days', int),
                    numCol<R>('Литров в день', 'liters_per_day', liters),
                  ]} />
              </Card>
            </Col>
          </Row>
        )}
      </QueryState>
    </>
  );
}
