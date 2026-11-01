import { Alert, Card, Col, Row, Table } from 'antd';
import { Link } from 'react-router-dom';
import { useFiltered } from '../api/client';
import { FilterBar } from '../components/FilterBar';
import { CHART_COLORS, ChartCard, KpiCard, numCol, PageTitle, QueryState } from '../components/ui';
import { bucketLabel, compact, liters, money, Num, pct } from '../lib/format';

type R = Record<string, any>;
interface Finance { filter: { g: string }; vat: { mode: string; rate: number }; fuel: R; shop: R; by_station: R[]; by_fuel: R[]; by_group: R[]; series: R[]; cash: R[] }

const coverageWarn = (v: Num) => (v != null && Number(v) < 0.999 ? ` · себестоимость известна для ${pct(v)}` : '');

export default function FinancePage() {
  const { data, error, isLoading, isFetching } = useFiltered<Finance>('/analytics/finance');
  const g = data?.filter.g ?? 'day';
  const fuel = data?.fuel ?? {};
  const shop = data?.shop ?? {};
  const margin = (Number(fuel.margin ?? 0) || 0) + (Number(shop.margin ?? 0) || 0);
  const noCosts = data && fuel.cost == null && shop.cost == null;
  const net = data?.vat?.mode === 'net';
  const revenue = Number(fuel.revenue ?? 0) + Number(shop.revenue ?? 0);
  const fiscal = Number(fuel.fiscal_revenue ?? 0) + Number(shop.fiscal_revenue ?? 0);
  const vatNote = net ? ` · без НДС ${Number(data!.vat.rate)} % из продаж с чеком` : ' · с НДС';

  return (
    <>
      <PageTitle title="Финансы" />
      <FilterBar compare={false} fuel />
      <QueryState error={error} loading={isLoading}>
        {data && (
          <>
            {noCosts && (
              <Alert type="info" showIcon style={{ marginBottom: 12 }}
                message="В Топазе нет закупочных цен — введите себестоимость, чтобы видеть маржу"
                description={<Link to="/costs">Перейти к вводу себестоимости топлива и товаров</Link>} />
            )}
            <Row gutter={[12, 12]}>
              {[
                { title: 'Выручка', value: Number(fuel.revenue ?? 0) + Number(shop.revenue ?? 0), format: money },
                { title: 'Валовая маржа', value: noCosts ? null : margin, format: money, hint: `Выручка − себестоимость по продажам, для которых известна закупочная цена${vatNote}` },
                { title: 'Маржа топливо', value: fuel.margin, format: money, hint: `${pct(fuel.margin_pct)} от выручки${coverageWarn(fuel.coverage)}` },
                { title: 'Маржа на литр', value: fuel.margin_per_liter, format: (v: Num) => money(v, true) },
                { title: 'Маржа магазин', value: shop.margin, format: money, hint: `наценка ${pct(shop.markup_pct)}${coverageWarn(shop.coverage)}` },
                { title: 'Скидки', value: fuel.discounts, format: money, hint: 'Скидки Топаза и скидки по дисконтным видам оплаты (цена заказа − цена колонки)' },
                { title: 'Выручка с чеком', value: revenue ? fiscal / revenue : null, format: pct, hint: `Доля выручки с фискальным чеком${net ? ': из неё вычитается НДС для маржи' : ''}` },
                { title: 'Отпуск без оплаты', value: fuel.service_cost, format: money, hint: `Тех. прокачки и переливы по закупочной цене: ${liters(fuel.service_liters)}` },
                { title: 'Покрытие себестоимостью', value: fuel.coverage, format: pct, hint: 'Доля проданных литров, для которых введена закупочная цена' },
              ].map((k) => <Col key={k.title} xs={12} md={8} xl={6}><KpiCard {...k} /></Col>)}
            </Row>
            <Row gutter={[12, 12]} style={{ marginTop: 12 }}>
              <Col xs={24} xl={14}>
                <ChartCard title="Выручка и маржа" loading={isFetching} empty={!data.series.length} option={{
                  color: [CHART_COLORS[0], CHART_COLORS[1], CHART_COLORS[4], CHART_COLORS[2]],
                  tooltip: { trigger: 'axis', valueFormatter: (v: any) => money(Number(v)) },
                  legend: { top: 0 },
                  grid: { left: 8, right: 8, bottom: 8, top: 36, containLabel: true },
                  xAxis: { type: 'category', data: data.series.map((s) => bucketLabel(s.bucket, g)) },
                  yAxis: [
                    { type: 'value', name: 'выручка', axisLabel: { formatter: (v: number) => compact(v) } },
                    { type: 'value', name: 'маржа', splitLine: { show: false }, axisLabel: { formatter: (v: number) => compact(v) } },
                  ],
                  series: [
                    { name: 'Выручка топливо', type: 'bar', stack: 'r', data: data.series.map((s) => s.fuel_revenue) },
                    { name: 'Выручка магазин', type: 'bar', stack: 'r', data: data.series.map((s) => s.shop_revenue) },
                    { name: 'Маржа топливо', type: 'line', yAxisIndex: 1, smooth: true, data: data.series.map((s) => s.fuel_margin) },
                    { name: 'Маржа магазин', type: 'line', yAxisIndex: 1, smooth: true, data: data.series.map((s) => s.shop_margin) },
                  ],
                }} />
              </Col>
              <Col xs={24} xl={10}>
                <Card size="small" title="Топливо" style={{ height: '100%' }}>
                  <Table size="small" rowKey="fuel" pagination={false} dataSource={data.by_fuel} scroll={{ x: 520 }}
                    columns={[
                      { title: 'Вид', dataIndex: 'fuel' },
                      numCol<R>('Литры', 'liters', liters),
                      numCol<R>('Выручка', 'revenue', money),
                      numCol<R>('Маржа', 'margin', money),
                      numCol<R>('На литр', 'margin_per_liter', (v) => money(v, true)),
                      numCol<R>('Покрытие', 'coverage', pct),
                    ]} />
                </Card>
              </Col>
              <Col xs={24} xl={12}>
                <Card size="small" title="По АЗС">
                  <Table size="small" rowKey="station_id" pagination={false} dataSource={data.by_station} scroll={{ x: 760 }}
                    columns={[
                      { title: 'АЗС', dataIndex: 'station', fixed: 'left', width: 170, ellipsis: true },
                      numCol<R>('Выручка', 'revenue', money),
                      numCol<R>('Маржа', 'margin', money, { defaultSortOrder: 'descend' }),
                      numCol<R>('Маржа топливо', 'fuel_margin', money),
                      numCol<R>('На литр', 'margin_per_liter', (v) => money(v, true)),
                      numCol<R>('Маржа магазин', 'shop_margin', money),
                      numCol<R>('Покрытие', 'fuel_coverage', pct),
                    ]} />
                </Card>
              </Col>
              <Col xs={24} xl={12}>
                <Card size="small" title="Магазин по группам">
                  <Table size="small" rowKey="group_name" pagination={false} dataSource={data.by_group} scroll={{ x: 560 }}
                    columns={[
                      { title: 'Группа', dataIndex: 'group_name' },
                      numCol<R>('Выручка', 'revenue', money),
                      numCol<R>('Себестоимость', 'cost', money),
                      numCol<R>('Маржа', 'margin', money),
                      numCol<R>('Наценка', 'markup_pct', pct),
                      numCol<R>('Покрытие', 'coverage', pct),
                    ]} />
                </Card>
              </Col>
              <Col xs={24}>
                <ChartCard title="Деньги по дням: наличные, безнал, инкассация" empty={!data.cash.length} option={{
                  color: [CHART_COLORS[4], CHART_COLORS[0], CHART_COLORS[3]],
                  tooltip: { trigger: 'axis', valueFormatter: (v: any) => money(Number(v)) },
                  legend: { top: 0 },
                  grid: { left: 8, right: 8, bottom: 8, top: 36, containLabel: true },
                  xAxis: { type: 'category', data: data.cash.map((c) => bucketLabel(c.day, 'day')) },
                  yAxis: { type: 'value', axisLabel: { formatter: (v: number) => compact(v) } },
                  series: [
                    { name: 'Наличные (топливо + магазин)', type: 'bar', stack: 'p', data: data.cash.map((c) => Number(c.fuel_cash) + Number(c.shop_cash)) },
                    { name: 'Безнал (топливо + магазин)', type: 'bar', stack: 'p', data: data.cash.map((c) => Number(c.fuel_cashless) + Number(c.shop_cashless)) },
                    { name: 'Выдано из кассы', type: 'line', data: data.cash.map((c) => c.cash_out) },
                  ],
                }} />
              </Col>
            </Row>
          </>
        )}
      </QueryState>
    </>
  );
}
