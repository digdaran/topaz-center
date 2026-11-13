import { DownloadOutlined, InfoCircleOutlined } from '@ant-design/icons';
import { Alert, Button, Card, Col, Row, Space, Table, Tag, Tooltip, Typography } from 'antd';
import { Link } from 'react-router-dom';
import { csvUrl, useFilter, useFiltered } from '../api/client';
import { FilterBar } from '../components/FilterBar';
import { CHART_COLORS, ChartCard, FigureList, numCol, QueryState } from '../components/ui';
import { PayBreakdown, usePayColor } from '../components/PayBreakdown';
import { bucketLabel, compact, int, liters, money, Num, pct } from '../lib/format';

type R = Record<string, any>;
interface Finance { filter: { g: string }; vat: { mode: string; rate: number }; fuel: R; shop: R; by_station: R[]; by_fuel: R[]; by_group: R[]; series: R[]; cash: R[] }

const BIG = { fontSize: 26, fontWeight: 600, fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap' } as const;

/** Margin as a share of revenue, small and grey next to the amount. */
const Share = ({ v }: { v: Num }) => (v == null ? null : <span style={{ opacity: 0.55, marginLeft: 8, fontSize: 12 }}>{pct(v)}</span>);

/** How much of the sales has a purchase price: a warning tag below 99.9 %. */
const Coverage = ({ v }: { v: Num }) => (v == null ? <>—</> : Number(v) < 0.999 ? <Tag color="orange" style={{ marginInlineEnd: 0 }}>{pct(v)}</Tag> : <>{pct(v)}</>);

export default function FinancePage({ view = 'margin' }: { view?: 'margin' | 'contracts' }) {
  const { data, error, isLoading, isFetching } = useFiltered<Finance>('/analytics/finance');
  const g = data?.filter.g ?? 'day';
  const fuel = data?.fuel ?? {};
  const shop = data?.shop ?? {};
  const margin = (Number(fuel.margin ?? 0) || 0) + (Number(shop.margin ?? 0) || 0);
  const noCosts = data && fuel.cost == null && shop.cost == null;
  const net = data?.vat?.mode === 'net';
  const revenue = Number(fuel.revenue ?? 0) + Number(shop.revenue ?? 0);
  const fiscal = Number(fuel.fiscal_revenue ?? 0) + Number(shop.fiscal_revenue ?? 0);
  const color = usePayColor();
  const sumOf = (k: string) => (data?.cash ?? []).reduce((a, c) => a + Number(c[k] ?? 0), 0);
  const vatNote = net ? ` · без НДС ${Number(data!.vat.rate)} % из продаж с чеком и по договорам` : ' · с НДС';

  return (
    <>
      <FilterBar compare={false} fuel pay />
      {view === 'contracts' ? <ContractSales /> : (
      <QueryState error={error} loading={isLoading}>
        {data && (
          <>
            {noCosts && (
              <Alert type="info" showIcon style={{ marginBottom: 12 }}
                message="В Топазе нет закупочных цен — введите себестоимость, чтобы видеть маржу"
                description={<Link to="/money/costs">Перейти к вводу себестоимости топлива и товаров</Link>} />
            )}
            <Card size="small" style={{ marginBottom: 12 }}>
              <Row gutter={[24, 12]}>
                <Col xs={24} md={8}>
                  <Typography.Text type="secondary">Выручка за период, всего</Typography.Text>
                  <div style={BIG}>{money(revenue)}</div>
                  <Typography.Text type="secondary">топливо {money(fuel.revenue)} · магазин {money(shop.revenue)}</Typography.Text>
                </Col>
                <Col xs={24} md={8}>
                  <Typography.Text type="secondary">Валовая маржа, всего <Tooltip title={`Выручка − себестоимость по продажам, для которых известна закупочная цена${vatNote}`}><InfoCircleOutlined /></Tooltip></Typography.Text>
                  <div style={BIG}>{noCosts ? '—' : money(margin)}</div>
                  <Typography.Text type="secondary">топливо {money(fuel.margin)} · магазин {money(shop.margin)}</Typography.Text>
                </Col>
                <Col xs={24} md={8}>
                  <FigureList rows={[
                    { key: 'vat', label: 'Маржа считается', value: net ? `без НДС ${Number(data.vat.rate)} %` : 'с НДС', hint: <>Меняется в <Link to="/settings/organization">Настройки → Маржа и НДС</Link></> },
                    { key: 'fiscal', label: 'Продажи с чеком или по договору', value: revenue ? pct(fiscal / revenue) : '—', hint: `Доля выручки с фискальным чеком или по договору (ведомости, топливные карты)${net ? ': из неё вычитается НДС для маржи' : ''}` },
                  ]} />
                </Col>
              </Row>
            </Card>
            <Row gutter={[12, 12]}>
              <Col xs={24} lg={12}>
                <Card size="small" title="Топливо" style={{ height: '100%' }}>
                  <FigureList rows={[
                    { key: 'r', label: 'Выручка', value: money(fuel.revenue) },
                    { key: 'c', label: 'Себестоимость проданного', value: money(fuel.cost), hint: 'Литры × средняя цена литра в резервуаре на момент продажи' },
                    { key: 'm', label: 'Валовая маржа', value: <>{money(fuel.margin)}<Share v={fuel.margin_pct} /></>, strong: true, hint: 'В процентах — доля маржи в выручке' },
                    { key: 'l', label: 'Маржа на литр', value: money(fuel.margin_per_liter, true) },
                    { key: 'd', label: 'Скидки клиентам', value: money(fuel.discounts), hint: 'Скидки Топаза и скидки по дисконтным видам оплаты (цена заказа − цена колонки); уже вычтены из выручки' },
                    { key: 's', label: 'Отпуск без оплаты', value: money(fuel.service_cost), hint: `Тех. прокачки и переливы по закупочной цене: ${liters(fuel.service_liters)}` },
                    { key: 'k', label: 'Себестоимость известна', value: <Coverage v={fuel.coverage} />, hint: 'Доля проданных литров, для которых введена закупочная цена. Остальные в маржу не входят' },
                  ]} />
                </Card>
              </Col>
              <Col xs={24} lg={12}>
                <Card size="small" title="Магазин" style={{ height: '100%' }}>
                  <FigureList rows={[
                    { key: 'r', label: 'Выручка', value: money(shop.revenue), hint: 'За вычетом возвратов' },
                    { key: 'c', label: 'Себестоимость проданного', value: money(shop.cost) },
                    { key: 'm', label: 'Валовая маржа', value: <>{money(shop.margin)}<Share v={shop.margin_pct} /></>, strong: true, hint: 'В процентах — доля маржи в выручке' },
                    { key: 'u', label: 'Наценка', value: pct(shop.markup_pct), hint: 'Маржа в процентах от себестоимости' },
                    { key: 'k', label: 'Себестоимость известна', value: <Coverage v={shop.coverage} />, hint: 'Доля выручки магазина по товарам с введённой себестоимостью' },
                  ]} />
                </Card>
              </Col>
            </Row>

            <Typography.Title level={5} style={{ marginTop: 20 }}>Как пришли деньги</Typography.Title>
            <Row gutter={[12, 12]}>
              <Col xs={24} xl={9}>
                <Card size="small" title="За период, топливо и магазин" style={{ height: '100%' }}>
                  <PayBreakdown exact={false} fuel={{ cash: sumOf('fuel_cash'), card: sumOf('fuel_card'), contract: sumOf('fuel_contract'), fuel_card: sumOf('fuel_fuel_card') }}
                    shop={{ cash: sumOf('shop_cash'), card: sumOf('shop_card'), contract: sumOf('shop_contract'), fuel_card: sumOf('shop_fuel_card') }} />
                  <Typography.Text type="secondary" style={{ display: 'block', marginTop: 12, marginBottom: 4 }}>Касса (наличные)</Typography.Text>
                  <FigureList rows={[
                    { key: 'in', label: 'Внесено в кассы (размен)', value: money(sumOf('cash_in')) },
                    { key: 'out', label: 'Выдано из касс (инкассация)', value: money(sumOf('cash_out')) },
                  ]} />
                </Card>
              </Col>
              <Col xs={24} xl={15}>
                <ChartCard title="По дням" empty={!data.cash.length} option={{
                  color: [color('cash'), color('card'), color('contract'), color('fuel_card'), CHART_COLORS[3]],
                  tooltip: { trigger: 'axis', valueFormatter: (v: any) => money(Number(v)) },
                  legend: { top: 0 },
                  grid: { left: 8, right: 8, bottom: 8, top: 36, containLabel: true },
                  xAxis: { type: 'category', data: data.cash.map((c) => bucketLabel(c.day, 'day')) },
                  yAxis: { type: 'value', axisLabel: { formatter: (v: number) => compact(v) } },
                  series: [
                    { name: 'Наличные', type: 'bar', stack: 'p', data: data.cash.map((c) => Number(c.fuel_cash) + Number(c.shop_cash)) },
                    { name: 'Банковские карты', type: 'bar', stack: 'p', data: data.cash.map((c) => Number(c.fuel_card) + Number(c.shop_card)) },
                    { name: 'По договорам', type: 'bar', stack: 'p', data: data.cash.map((c) => Number(c.fuel_contract) + Number(c.shop_contract)) },
                    { name: 'Топливные карты', type: 'bar', stack: 'p', data: data.cash.map((c) => Number(c.fuel_fuel_card) + Number(c.shop_fuel_card ?? 0)) },
                    { name: 'Выдано из касс', type: 'line', data: data.cash.map((c) => c.cash_out) },
                  ],
                }} />
              </Col>
            </Row>

            <Typography.Title level={5} style={{ marginTop: 20 }}>Маржа по дням, видам топлива, АЗС и группам товаров</Typography.Title>
            <Row gutter={[12, 12]}>
              <Col xs={24} xl={14}>
                <ChartCard title="Выручка и маржа" loading={isFetching} empty={!data.series.length} option={{
                  color: [CHART_COLORS[0], CHART_COLORS[1], CHART_COLORS[4], CHART_COLORS[2]],
                  tooltip: { trigger: 'axis', valueFormatter: (v: any) => money(Number(v)) },
                  legend: { top: 0 },
                  grid: { left: 8, right: 8, bottom: 8, top: 36, containLabel: true },
                  xAxis: { type: 'category', data: data.series.map((s) => bucketLabel(s.bucket, g)) },
                  yAxis: [
                    { type: 'value', axisLabel: { formatter: (v: number) => compact(v) } },
                    { type: 'value', splitLine: { show: false }, axisLabel: { formatter: (v: number) => compact(v) } },
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
                <Card size="small" title="Топливо по видам" style={{ height: '100%' }}>
                  <Table size="small" rowKey="fuel" pagination={false} dataSource={data.by_fuel} scroll={{ x: 'max-content' }}
                    columns={[
                      { title: 'Вид', dataIndex: 'fuel' },
                      numCol<R>('Литры', 'liters', liters),
                      numCol<R>('Выручка', 'revenue', money),
                      numCol<R>('Маржа', 'margin', money),
                      numCol<R>('На литр', 'margin_per_liter', (v) => money(v, true)),
                      { ...numCol<R>('Покрытие', 'coverage', pct), title: <Tooltip title="Доля литров с известной себестоимостью">Покрытие</Tooltip> },
                    ]} />
                </Card>
              </Col>
              <Col xs={24} xl={12}>
                <Card size="small" title="По АЗС">
                  <Table size="small" rowKey="station_id" pagination={false} dataSource={data.by_station} bordered scroll={{ x: 'max-content' }}
                    columns={[
                      { title: 'АЗС', dataIndex: 'station', fixed: 'left' },
                      { title: 'Всего', key: 'all', children: [numCol<R>('Выручка', 'revenue', money), numCol<R>('Маржа', 'margin', money, { defaultSortOrder: 'descend' })] },
                      { title: 'Топливо', key: 'fuel', children: [numCol<R>('Маржа', 'fuel_margin', money), numCol<R>('На литр', 'margin_per_liter', (v) => money(v, true)), { ...numCol<R>('Покрытие', 'fuel_coverage', pct), title: <Tooltip title="Доля литров с известной себестоимостью">Покрытие</Tooltip> }] },
                      { title: 'Магазин', key: 'shop', children: [numCol<R>('Маржа', 'shop_margin', money)] },
                    ]} />
                </Card>
              </Col>
              <Col xs={24} xl={12}>
                <Card size="small" title="Магазин по группам">
                  <Table size="small" rowKey="group_name" pagination={false} dataSource={data.by_group} scroll={{ x: 'max-content' }}
                    columns={[
                      { title: 'Группа', dataIndex: 'group_name' },
                      numCol<R>('Выручка', 'revenue', money),
                      numCol<R>('Себестоимость', 'cost', money),
                      numCol<R>('Маржа', 'margin', money),
                      numCol<R>('Наценка', 'markup_pct', pct),
                      { ...numCol<R>('Покрытие', 'coverage', pct), title: <Tooltip title="Доля выручки по товарам с известной себестоимостью">Покрытие</Tooltip> },
                    ]} />
                </Card>
              </Col>
            </Row>
          </>
        )}
      </QueryState>
      )}
    </>
  );
}

interface ContractRow { station: string; month: string; category: string; payment_mode: string; fuel: string; liters: number; amount: number; orders: number }

/** Fuel sold on contract (ведомости) and by fuel cards: what the clients and card issuers owe for the period. */
function ContractSales() {
  const [filter] = useFilter();
  const { data, error, isLoading } = useFiltered<{ rows: ContractRow[]; totals: { category: string; liters: number; amount: number; orders: number }[] }>('/analytics/contracts');
  const months = (m: string) => { const [y, mm] = m.split('-'); return `${mm}.${y}`; };
  return (
    <QueryState error={error} loading={isLoading}>
      {data && (
        <>
          <Typography.Paragraph type="secondary">
            Отпуски по видам оплаты со способом «По договору» (ведомости) и «Топливные карты» — деньги за них приходят на счёт позже.
            Способ оплаты у каждого вида оплаты можно проверить в <Link to="/settings/payment-modes">Настройки → Виды оплаты</Link>.
          </Typography.Paragraph>
          <Space wrap style={{ marginBottom: 12 }}>
            {data.totals.map((t) => (
              <Card key={t.category} size="small" style={{ minWidth: 220 }}>
                <Typography.Text type="secondary">{t.category}</Typography.Text>
                <div style={{ fontSize: 20, fontWeight: 600 }}>{money(t.amount)}</div>
                <Typography.Text type="secondary">{liters(t.liters)} · {int(t.orders)} отпусков</Typography.Text>
              </Card>
            ))}
            <Button icon={<DownloadOutlined />} href={csvUrl('/analytics/contracts', filter)}>Скачать CSV (Excel)</Button>
          </Space>
          <Table<ContractRow> size="small" rowKey={(r) => `${r.station}/${r.month}/${r.category}/${r.payment_mode}/${r.fuel}`} dataSource={data.rows}
            pagination={{ pageSize: 50 }} scroll={{ x: 800 }}
            locale={{ emptyText: 'За период нет продаж по договорам и топливным картам' }}
            columns={[
              { title: 'АЗС', dataIndex: 'station' },
              { title: 'Месяц', dataIndex: 'month', render: months },
              { title: 'Способ', dataIndex: 'category', render: (v: string) => <Tag color={v === 'По договору' ? 'purple' : 'gold'}>{v}</Tag> },
              { title: 'Вид оплаты в Топазе', dataIndex: 'payment_mode' },
              { title: 'Топливо', dataIndex: 'fuel' },
              numCol<ContractRow>('Литры', 'liters', liters),
              numCol<ContractRow>('Сумма', 'amount', money),
              numCol<ContractRow>('Отпусков', 'orders', int),
            ]} />
        </>
      )}
    </QueryState>
  );
}
