import { Card, Col, Row, Table, Tag, Tooltip, Typography } from 'antd';
import { useFiltered } from '../api/client';
import { FilterBar } from '../components/FilterBar';
import { CHART_COLORS, ChartCard, numCol, PageTitle, QueryState } from '../components/ui';
import { bucketLabel, compact, dec, int, liters, money, PAYMENT_KIND, pct } from '../lib/format';

type R = Record<string, any>;
interface Fuel {
  filter: { g: string };
  by_type: R[]; series: R[]; payments: R[]; fills: R[]; order_types: R[]; hoses: R[]; service: R[]; price_changes: R[]; price_history: R[];
}

const FILL_BINS = ['до 10 л', '10–20', '20–30', '30–40', '40–50', '50–70', '70–100', '100+ л'];
const ORDER_KIND: Record<string, string> = { full_tank: 'Полный бак', by_liters: 'На литры', by_amount: 'На сумму', other: 'Прочие' };
const TYPE_NAME: Record<string, string> = { AI92: 'АИ-92', AI95: 'АИ-95', AI98: 'АИ-98/100', DT: 'ДТ', LPG: 'СУГ', CNG: 'Метан', OTHER: 'Прочее' };

export default function FuelPage() {
  const { data, error, isLoading, isFetching } = useFiltered<Fuel>('/analytics/fuel');
  const g = data?.filter.g ?? 'day';
  const buckets = [...new Set((data?.series ?? []).map((s) => s.bucket))].sort();
  const types = [...new Set((data?.series ?? []).map((s) => s.fuel_type))];
  const total = (data?.by_type ?? []).reduce((a, t) => a + Number(t.revenue ?? 0), 0);

  return (
    <>
      <PageTitle title="Топливо" />
      <FilterBar compare={false} />
      <QueryState error={error} loading={isLoading}>
        {data && (
          <>
            <Row gutter={[12, 12]}>
              {data.by_type.map((t) => (
                <Col key={t.fuel_type} xs={12} md={8} xl={Math.max(4, Math.floor(24 / Math.max(1, data.by_type.length)))}>
                  <Card size="small" title={t.fuel_name} extra={<Typography.Text type="secondary">{pct(total ? t.revenue / total : 0)}</Typography.Text>}>
                    <div style={{ fontSize: 20, fontWeight: 600, whiteSpace: 'nowrap', fontVariantNumeric: 'tabular-nums' }}>{liters(t.liters)}</div>
                    <div style={{ fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap' }}>{money(t.revenue)}</div>
                    <Typography.Text type="secondary" style={{ fontSize: 12, whiteSpace: 'nowrap' }}>
                      ср. цена {money(t.avg_price, true)} · налив {liters(t.avg_fill)}
                    </Typography.Text>
                  </Card>
                </Col>
              ))}
            </Row>
            <Row gutter={[12, 12]} style={{ marginTop: 12 }}>
              <Col xs={24} xl={16}>
                <ChartCard title="Продажи по видам топлива, литры" loading={isFetching} empty={!buckets.length} option={{
                  color: CHART_COLORS,
                  tooltip: { trigger: 'axis', valueFormatter: (v) => liters(Number(v)) },
                  legend: { top: 0 },
                  grid: { left: 8, right: 8, bottom: 8, top: 36, containLabel: true },
                  xAxis: { type: 'category', data: buckets.map((b) => bucketLabel(b, g)) },
                  yAxis: { type: 'value', axisLabel: { formatter: (v: number) => compact(v) } },
                  series: types.map((t) => ({
                    name: TYPE_NAME[t] ?? t, type: 'bar', stack: 'l',
                    data: buckets.map((b) => data.series.find((s) => s.bucket === b && s.fuel_type === t)?.liters ?? 0),
                  })),
                }} />
              </Col>
              <Col xs={24} xl={8}>
                <ChartCard title="Как заправляются" empty={!data.order_types.length} option={{
                  color: CHART_COLORS,
                  tooltip: { trigger: 'item', formatter: (x: any) => `${x.name}: ${int(x.value)} заказов (${x.percent}%)` },
                  legend: { bottom: 0 },
                  series: [{
                    type: 'pie', radius: ['40%', '65%'], center: ['50%', '45%'], label: { formatter: '{b}\n{d}%' },
                    data: data.order_types.map((o) => ({ name: ORDER_KIND[o.kind] ?? o.kind, value: o.orders })),
                  }],
                }} />
              </Col>
              <Col xs={24} lg={12}>
                <ChartCard title="Объём налива" empty={!data.fills.length} option={{
                  color: [CHART_COLORS[1]],
                  tooltip: { trigger: 'axis', formatter: (p: any) => `${p[0].name}: ${int(p[0].value)} заказов` },
                  grid: { left: 8, right: 8, bottom: 8, top: 16, containLabel: true },
                  xAxis: { type: 'category', data: FILL_BINS },
                  yAxis: { type: 'value' },
                  series: [{ type: 'bar', data: FILL_BINS.map((_, i) => data.fills.find((f) => f.bin === i + 1)?.orders ?? 0) }],
                }} />
              </Col>
              <Col xs={24} lg={12}>
                <Card size="small" title="Оплата" style={{ height: '100%' }}>
                  <Table size="small" rowKey={(r) => `${r.payment_kind}/${r.payment_mode}`} pagination={false} dataSource={data.payments} scroll={{ x: 500 }}
                    columns={[
                      { title: 'Вид оплаты', dataIndex: 'payment_mode' },
                      { title: 'Способ', dataIndex: 'payment_kind', render: (v: string) => <Tag color={v === 'cashless' ? 'blue' : v === 'cash' ? 'green' : 'default'}>{PAYMENT_KIND[v] ?? v}</Tag> },
                      numCol<R>('Литры', 'liters', liters),
                      numCol<R>('Выручка', 'revenue', money),
                      numCol<R>('Заказы', 'orders', int),
                      numCol<R>('Скидки', 'discounts', money),
                    ]} />
                </Card>
              </Col>
            </Row>

            <Typography.Title level={4} style={{ marginTop: 20 }}>Цены и спрос</Typography.Title>
            <Row gutter={[12, 12]}>
              <Col xs={24} xl={12}>
                <ChartCard title="Цена по дням" empty={!data.price_history.length} option={(() => {
                  const days = [...new Set(data.price_history.map((p) => p.day))].sort();
                  const pts = [...new Set(data.price_history.map((p) => p.fuel_type))];
                  return {
                    color: CHART_COLORS,
                    tooltip: { trigger: 'axis', valueFormatter: (v: any) => money(Number(v), true) },
                    legend: { top: 0 },
                    grid: { left: 8, right: 8, bottom: 8, top: 36, containLabel: true },
                    xAxis: { type: 'category', data: days.map((d) => bucketLabel(d, 'day')) },
                    yAxis: { type: 'value', scale: true, axisLabel: { formatter: (v: number) => `${v} ₽` } },
                    series: pts.map((t) => ({
                      name: TYPE_NAME[t] ?? t, type: 'line', step: 'end', symbol: 'none',
                      data: days.map((d) => data.price_history.find((p) => p.day === d && p.fuel_type === t)?.price ?? null), connectNulls: true,
                    })),
                  };
                })()} />
              </Col>
              <Col xs={24} xl={12}>
                <Card size="small" title={<>Реакция спроса на изменение цены <Tooltip title="Средние литры в день за 14 дней до и после изменения цены. Эластичность = изменение объёма, % / изменение цены, %. Оценка: на спрос влияют и сезон, и погода, и конкуренты."><Typography.Text type="secondary">(?)</Typography.Text></Tooltip></>} style={{ height: '100%' }}>
                  <Table size="small" rowKey={(r) => `${r.station_id}/${r.fuel_type}/${r.date}`} dataSource={[...data.price_changes].sort((a, b) => b.date.localeCompare(a.date))}
                    pagination={{ pageSize: 8 }} scroll={{ x: 820 }}
                    locale={{ emptyText: 'Изменений цены за период нет' }}
                    columns={[
                      { title: 'Дата', dataIndex: 'date', render: (v: string) => v.split('-').reverse().join('.') },
                      { title: 'АЗС', dataIndex: 'station', width: 170, ellipsis: true },
                      { title: 'Топливо', dataIndex: 'fuel_type', render: (v: string) => TYPE_NAME[v] ?? v },
                      { title: 'Цена', key: 'p', render: (_, r) => <span style={{ whiteSpace: 'nowrap' }}>{dec(r.price_before)} → {dec(r.price_after)}</span> },
                      numCol<R>('Δ цены', 'price_change', pct),
                      { title: 'Литров/день', key: 'l', align: 'right', render: (_, r) => <span style={{ whiteSpace: 'nowrap' }}>{int(r.liters_per_day_before)} → {int(r.liters_per_day_after)}</span> },
                      { ...numCol<R>('Δ объёма', 'volume_change', pct), render: (v: number) => <span style={{ color: v < 0 ? '#cf1322' : '#389e0d' }}>{pct(v)}</span> },
                      numCol<R>('Эластичность', 'elasticity', (v) => dec(v, 2)),
                    ]} />
                </Card>
              </Col>
            </Row>

            <Row gutter={[12, 12]} style={{ marginTop: 12 }}>
              <Col xs={24} xl={14}>
                <Card size="small" title="Загрузка ТРК по рукавам">
                  <Table size="small" rowKey={(r) => `${r.station_id}/${r.hose_num}/${r.fuel_name}`} dataSource={data.hoses} pagination={{ pageSize: 10 }} scroll={{ x: 600 }}
                    columns={[
                      { title: 'АЗС', dataIndex: 'station' },
                      { title: 'Рукав', dataIndex: 'hose_num' },
                      { title: 'Резервуар', dataIndex: 'tank_num' },
                      { title: 'Топливо', dataIndex: 'fuel_name' },
                      numCol<R>('Заказы', 'orders', int, { defaultSortOrder: 'descend' }),
                      numCol<R>('Литры', 'liters', liters),
                      { ...numCol<R>('Прерв.', 'aborted', int), render: (v: number) => (v > 0 ? <Tag color="orange">{v}</Tag> : 0) },
                    ]} />
                </Card>
              </Col>
              <Col xs={24} xl={10}>
                <Card size="small" title="Отпуск без оплаты (тех. прокачки, переливы)">
                  <Table size="small" rowKey={(r) => `${r.station}/${r.fuel_name}/${r.payment_mode}`} dataSource={data.service} pagination={{ pageSize: 10 }}
                    locale={{ emptyText: 'Нет' }}
                    columns={[
                      { title: 'АЗС', dataIndex: 'station' },
                      { title: 'Топливо', dataIndex: 'fuel_name' },
                      { title: 'Вид', dataIndex: 'payment_mode' },
                      numCol<R>('Операций', 'operations', int),
                      numCol<R>('Литры', 'liters', liters),
                    ]} />
                </Card>
              </Col>
            </Row>
          </>
        )}
      </QueryState>
    </>
  );
}
