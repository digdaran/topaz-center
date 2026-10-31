import { WarningOutlined } from '@ant-design/icons';
import { Alert, Card, Col, Row, Table, Tag, Typography } from 'antd';
import { Link, useLocation } from 'react-router-dom';
import { useFiltered } from '../api/client';
import { FilterBar } from '../components/FilterBar';
import { CHART_COLORS, ChartCard, KpiCard, numCol, PageTitle, QueryState } from '../components/ui';
import { bucketLabel, compact, dateTime, int, liters, money, pct } from '../lib/format';
import { LatestAlerts } from './ControlPage';

export interface Kpi {
  fuel_revenue: number; liters: number; orders: number; aborted: number; service_liters: number; cashless: number; mixed: number;
  discounts: number; shop_revenue: number; shop_receipts: number; shop_items: number; revenue: number; avg_fill: number | null;
  avg_fuel_check: number | null; avg_shop_check: number | null; cashless_share: number | null; aborted_share: number | null;
  margin: number | null; fuel_coverage: number | null;
}

export interface StationRow {
  station_id: string; station: string; region: string | null; fuel_revenue: number; liters: number; orders: number; aborted: number;
  cashless: number; discounts: number; shop_revenue: number; shop_receipts: number; revenue: number; liters_per_day: number;
  avg_fill: number | null; avg_fuel_check: number | null; avg_shop_check: number | null; cashless_share: number | null; shop_to_fuel: number | null;
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

export default function OverviewPage() {
  const { data, error, isLoading, isFetching } = useFiltered<Overview>('/analytics/overview');
  const location = useLocation();
  const c = data?.current;
  const p = data?.previous ?? undefined;
  const g = data?.filter.g ?? 'day';

  const kpis = c && [
    { title: 'Выручка', value: c.revenue, previous: p?.revenue, format: money, hint: 'Топливо + магазин, по итоговой стоимости после скидок' },
    { title: 'Выручка топливо', value: c.fuel_revenue, previous: p?.fuel_revenue, format: money },
    { title: 'Выручка магазин', value: c.shop_revenue, previous: p?.shop_revenue, format: money },
    ...(c.margin != null ? [{ title: 'Валовая маржа', value: c.margin, previous: p?.margin, format: money, hint: `По продажам с известной себестоимостью${c.fuel_coverage != null && c.fuel_coverage < 0.999 ? ` (топливо: ${pct(c.fuel_coverage)})` : ''}` }] : []),
    { title: 'Продано топлива', value: c.liters, previous: p?.liters, format: liters, hint: 'Без тех. прокачек и переливов' },
    { title: 'Заказы топлива', value: c.orders, previous: p?.orders, format: int, hint: 'Наливы с объёмом > 0, без тех. прокачек' },
    { title: 'Средний налив', value: c.avg_fill, previous: p?.avg_fill, format: liters },
    { title: 'Средний чек топливо', value: c.avg_fuel_check, previous: p?.avg_fuel_check, format: money },
    { title: 'Средний чек магазин', value: c.avg_shop_check, previous: p?.avg_shop_check, format: money },
    { title: 'Доля безнала', value: c.cashless_share, previous: p?.cashless_share, format: pct, hint: 'Доля выручки топлива, оплаченной безналом (пометка «Б»)' },
    { title: 'Скидки', value: c.discounts, previous: p?.discounts, format: money, inverse: true },
    { title: 'Прерванные наливы', value: c.aborted, previous: p?.aborted, format: int, inverse: true, hint: 'Заказы с нулевым объёмом' },
    { title: 'Тех. прокачки', value: c.service_liters, previous: p?.service_liters, format: liters, inverse: true, hint: 'Отпуск без оплаты: тех. прокачки, переливы' },
  ];

  const series = data?.series ?? [];
  return (
    <>
      <PageTitle title="Обзор" />
      <FilterBar fuel />
      <QueryState error={error} loading={isLoading}>
        {data && (
          <>
            {data.problems.length > 0 && (
              <Alert
                type="warning"
                showIcon
                icon={<WarningOutlined />}
                style={{ marginBottom: 16 }}
                message={`Проблемы со связью или данными: ${data.problems.length} АЗС`}
                description={data.problems.map((s) => (
                  <div key={s.station_id}>
                    <Link to={`/stations${location.search}`}>{s.station}</Link>
                    {': '}
                    {s.silent_seconds === null ? 'ещё не выходила на связь' : s.silent_seconds > 900 ? `нет связи с ${dateTime(s.last_seen_at)}` : ''}
                    {s.topaz_db_ok === false && ' · нет доступа к базе Топаза'}
                    {(s.agent_queue_dead ?? 0) > 0 && ` · отклонённых пакетов: ${s.agent_queue_dead}`}
                  </div>
                ))}
              />
            )}
            <Row gutter={[12, 12]}>
              {kpis!.map((k) => (
                <Col key={k.title} xs={12} sm={8} md={6} xl={4}>
                  <KpiCard {...k} previous={data.previous ? k.previous : undefined} />
                </Col>
              ))}
            </Row>
            <LatestAlerts />
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
                scroll={{ x: 1250 }}
                dataSource={data.stations}
                columns={[
                  {
                    title: 'АЗС', dataIndex: 'station', key: 'station', fixed: 'left', width: 220,
                    render: (v: string, r) => <span style={{ whiteSpace: 'nowrap' }}>{v}{r.region && <Typography.Text type="secondary"> · {r.region}</Typography.Text>}</span>,
                  },
                  numCol<StationRow>('Выручка', 'revenue', money, { defaultSortOrder: 'descend' }),
                  numCol<StationRow>('Топливо', 'fuel_revenue', money),
                  numCol<StationRow>('Магазин', 'shop_revenue', money),
                  numCol<StationRow>('Литры', 'liters', liters),
                  numCol<StationRow>('Литров в день', 'liters_per_day', liters),
                  numCol<StationRow>('Заказы', 'orders', int),
                  numCol<StationRow>('Средний налив', 'avg_fill', liters),
                  numCol<StationRow>('Ср. чек топливо', 'avg_fuel_check', money),
                  numCol<StationRow>('Ср. чек магазин', 'avg_shop_check', money),
                  numCol<StationRow>('Безнал', 'cashless_share', pct),
                  { ...numCol<StationRow>('Прерв. наливы', 'aborted', int), render: (v: number) => (v > 0 ? <Tag color="orange">{int(v)}</Tag> : '0') },
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
