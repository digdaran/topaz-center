import { Alert, Card, Col, Flex, InputNumber, Row, Select, Table, Tabs, Tag, Tooltip, Typography } from 'antd';
import { useState } from 'react';
import { useFiltered, useMeta } from '../api/client';
import { FilterBar } from '../components/FilterBar';
import { CHART_COLORS, ChartCard, numCol, QueryState } from '../components/ui';
import { dateTime, dec, int, money, pct } from '../lib/format';

type R = Record<string, any>;
interface Items {
  cover_days: number; dead_days: number; full_weeks: number;
  groups: R[]; items: R[]; matrix: { abc: string; xyz: string; items: number; revenue: number }[]; stock: R[]; shop_vs_fuel: R[]; returns: R[]; write_offs: R[];
}

const ABC_COLOR: Record<string, string> = { A: 'green', B: 'gold', C: 'default' };
const XYZ_HELP: Record<string, string> = { X: 'стабильный спрос', Y: 'колеблется', Z: 'нерегулярный', '—': 'мало недель для оценки' };
const STATUS: Record<string, { label: string; color: string }> = {
  out: { label: 'нет в наличии', color: 'red' },
  low: { label: 'заканчивается', color: 'orange' },
  dead: { label: 'неликвид', color: 'purple' },
  overstock: { label: 'избыток', color: 'blue' },
  ok: { label: 'норма', color: 'green' },
};

function Matrix({ data }: { data: Items }) {
  const cell = (a: string, x: string) => data.matrix.find((m) => m.abc === a && m.xyz === x);
  const advice: Record<string, string> = {
    AX: 'Основа ассортимента: держать всегда, автозаказ', AY: 'Важные, спрос колеблется: страховой запас', AZ: 'Важные, но непредсказуемые: следить вручную',
    BX: 'Стабильные середнячки: регулярный заказ', BY: 'Средние: заказ по факту', BZ: 'Под заказ, небольшой запас',
    CX: 'Мелкие, но стабильные: минимальный запас', CY: 'Кандидаты на сокращение', CZ: 'Кандидаты на вывод из ассортимента',
  };
  return (
    <Card size="small" title={<>Матрица ABC × XYZ <Typography.Text type="secondary" style={{ fontWeight: 400 }}>· ABC по выручке (A — 80 %, B — следующие 15 %), XYZ по колебаниям недельных продаж ({data.full_weeks} полных недель)</Typography.Text></>}>
      {data.full_weeks < 4 && <Alert type="info" showIcon style={{ marginBottom: 8 }} message="Для XYZ нужен период не меньше 4 полных недель — выберите период длиннее." />}
      <table style={{ width: '100%', borderCollapse: 'separate', borderSpacing: 6 }}>
        <thead><tr><th />{['X', 'Y', 'Z'].map((x) => <th key={x}>{x} <Typography.Text type="secondary" style={{ fontWeight: 400, fontSize: 12 }}>{XYZ_HELP[x]}</Typography.Text></th>)}</tr></thead>
        <tbody>
          {['A', 'B', 'C'].map((a) => (
            <tr key={a}>
              <th style={{ width: 40 }}><Tag color={ABC_COLOR[a]}>{a}</Tag></th>
              {['X', 'Y', 'Z'].map((x) => {
                const c = cell(a, x);
                return (
                  <td key={x} style={{ border: '1px solid rgba(128,128,128,.25)', borderRadius: 8, padding: 8, verticalAlign: 'top', width: '31%' }}>
                    <div style={{ fontSize: 18, fontWeight: 600 }}>{c ? `${c.items} поз.` : '—'}</div>
                    <div style={{ opacity: 0.7 }}>{c ? money(c.revenue) : ''}</div>
                    <div style={{ fontSize: 12, opacity: 0.65, marginTop: 4 }}>{advice[a + x]}</div>
                  </td>
                );
              })}
            </tr>
          ))}
        </tbody>
      </table>
    </Card>
  );
}

function Stock({ data, cover, setCover, dead, setDead }: { data: Items; cover: number; setCover: (v: number) => void; dead: number; setDead: (v: number) => void }) {
  const [status, setStatus] = useState<string | undefined>();
  const meta = useMeta();
  const rows = data.stock.filter((s) => !status || s.status === status);
  const many = (meta.data?.stations.length ?? 0) > 1;
  return (
    <>
      <Flex wrap gap={12} align="center" style={{ marginBottom: 8 }}>
        <span>Заказ на <InputNumber min={1} max={120} value={cover} onChange={(v) => v && setCover(v)} style={{ width: 70 }} /> дней</span>
        <span>Неликвид: нет продаж <InputNumber min={7} max={365} value={dead} onChange={(v) => v && setDead(v)} style={{ width: 70 }} /> дней</span>
        <Select allowClear placeholder="Все статусы" style={{ width: 180 }} value={status} onChange={setStatus}
          options={Object.entries(STATUS).map(([k, v]) => ({ value: k, label: `${v.label} (${data.stock.filter((s) => s.status === k).length})` }))} />
        <Typography.Text type="secondary">Скорость продаж — за последние 28 дней; остатки — сейчас.</Typography.Text>
      </Flex>
      <Table size="small" rowKey={(r) => `${r.station_id}/${r.item_key}`} dataSource={rows} pagination={{ pageSize: 50 }} scroll={{ x: 1100 }}
        columns={[
          ...(many ? [{ title: 'АЗС', dataIndex: 'station', key: 'st' }] : []),
          { title: 'Товар', dataIndex: 'name', key: 'n' },
          { title: 'Группа', dataIndex: 'group_name', key: 'g' },
          { title: 'Статус', dataIndex: 'status', key: 's', render: (v: string) => <Tag color={STATUS[v]?.color}>{STATUS[v]?.label ?? v}</Tag> },
          numCol<R>('Остаток', 'stock', (v) => dec(v, 0)),
          numCol<R>('Продаётся в день', 'per_day', (v) => dec(v, 2)),
          numCol<R>('Хватит на, дней', 'days_of_cover', (v) => dec(v, 1)),
          { title: 'Закончится', dataIndex: 'stockout_date', key: 'so', render: (v: string | null) => v?.split('-').reverse().join('.') ?? '—' },
          { ...numCol<R>('Рекомендуемый заказ', 'recommended_order', (v) => dec(v, 0)), render: (v: number) => (v > 0 ? <b>{dec(v, 0)}</b> : '—') },
          { title: 'Последняя продажа', dataIndex: 'last_sale', key: 'ls', render: (v: string | null) => (v ? dateTime(v).slice(0, 10) : 'не было') },
          numCol<R>('Сумма остатка', 'stock_value', money),
        ]} />
    </>
  );
}

function Returns({ data }: { data: Items }) {
  return (
    <Row gutter={[12, 12]}>
      <Col xs={24} lg={12}>
        <Card size="small" title="Возвраты покупателей">
          <Table size="small" rowKey="item_name" pagination={false} dataSource={data.returns} locale={{ emptyText: 'Возвратов нет' }}
            columns={[{ title: 'Товар', dataIndex: 'item_name' }, numCol<R>('Кол-во', 'quantity', (v) => dec(v, 0)), numCol<R>('Сумма', 'amount', money)]} />
        </Card>
      </Col>
      <Col xs={24} lg={12}>
        <Card size="small" title="Списания (акты ТМЦ)">
          <Table size="small" rowKey={(r) => `${r.station}/${r.name}`} pagination={false} dataSource={data.write_offs} locale={{ emptyText: 'Списаний нет' }}
            columns={[{ title: 'АЗС', dataIndex: 'station' }, { title: 'Товар', dataIndex: 'name' }, numCol<R>('Кол-во', 'quantity', (v) => dec(v, 0)), numCol<R>('Актов', 'acts', int)]} />
        </Card>
      </Col>
    </Row>
  );
}

export default function ItemsPage({ view = 'sales' }: { view?: 'sales' | 'stock' }) {
  const [cover, setCover] = useState(14);
  const [dead, setDead] = useState(30);
  const { data, error, isLoading } = useFiltered<Items>('/analytics/items', { cover_days: cover, dead_days: dead });

  return (
    <>
      <FilterBar granularity={false} compare={false} />
      <QueryState error={error} loading={isLoading}>
        {data && (
          <Tabs items={view === 'stock' ? [
            { key: 'stock', label: `Остатки${data.stock.some((s) => s.status === 'out' || s.status === 'low') ? ' ⚠' : ''}`, children: <Stock data={data} cover={cover} setCover={setCover} dead={dead} setDead={setDead} /> },
            { key: 'returns', label: 'Возвраты и списания', children: <Returns data={data} /> },
          ] : [
            {
              key: 'sales', label: 'Продажи', children: (
                <Row gutter={[12, 12]}>
                  <Col xs={24} lg={9}>
                    <ChartCard title="Выручка по группам" empty={!data.groups.length} option={{
                      color: CHART_COLORS,
                      tooltip: { trigger: 'item', formatter: (x: any) => `${x.name}<br/>${money(x.value)} (${x.percent}%)` },
                      legend: { bottom: 0 },
                      series: [{ type: 'pie', radius: ['40%', '65%'], center: ['50%', '45%'], label: { formatter: '{b}\n{d}%' }, data: data.groups.map((g) => ({ name: g.group_name, value: g.revenue })) }],
                    }} />
                  </Col>
                  <Col xs={24} lg={15}>
                    <Card size="small" title="Товары" style={{ height: '100%' }}>
                      <Table size="small" rowKey="item_key" dataSource={data.items} pagination={{ pageSize: 15 }} scroll={{ x: 800 }}
                        columns={[
                          { title: 'Товар', dataIndex: 'name' },
                          { title: 'Группа', dataIndex: 'group_name' },
                          { title: 'ABC', dataIndex: 'abc', filters: ['A', 'B', 'C'].map((v) => ({ text: v, value: v })), onFilter: (v, r) => r.abc === v, render: (v: string) => <Tag color={ABC_COLOR[v]}>{v}</Tag> },
                          { title: 'XYZ', dataIndex: 'xyz', filters: ['X', 'Y', 'Z'].map((v) => ({ text: v, value: v })), onFilter: (v, r) => r.xyz === v, render: (v: string, r) => <Tooltip title={`${XYZ_HELP[v] ?? ''}${r.cv != null ? `, коэфф. вариации ${pct(r.cv)}` : ''}`}>{v}</Tooltip> },
                          numCol<R>('Выручка', 'revenue', money, { defaultSortOrder: 'descend' }),
                          numCol<R>('Доля', 'share', pct),
                          numCol<R>('Продано', 'quantity', (v) => dec(v, 0)),
                          numCol<R>('Средняя цена', 'avg_price', (v) => money(v, true)),
                          numCol<R>('Чеков', 'receipts', int),
                        ]} />
                    </Card>
                  </Col>
                  <Col xs={24}>
                    <ChartCard title="Магазин и топливо по часам: сколько чеков магазина приходится на заказ топлива" empty={!data.shop_vs_fuel.length} option={{
                      color: [CHART_COLORS[0], CHART_COLORS[2]],
                      tooltip: { trigger: 'axis' },
                      legend: { top: 0 },
                      grid: { left: 8, right: 8, bottom: 8, top: 36, containLabel: true },
                      xAxis: { type: 'category', data: data.shop_vs_fuel.map((h) => `${String(h.hour).padStart(2, '0')}:00`) },
                      yAxis: [{ type: 'value' }, { type: 'value', axisLabel: { formatter: (v: number) => `${Math.round(v * 100)}%` }, splitLine: { show: false } }],
                      series: [
                        { name: 'Чеки магазина', type: 'bar', data: data.shop_vs_fuel.map((h) => h.shop_receipts) },
                        { name: 'Чеков на заказ топлива', type: 'line', yAxisIndex: 1, smooth: true, data: data.shop_vs_fuel.map((h) => h.ratio) },
                      ],
                    }} />
                  </Col>
                </Row>
              ),
            },
            { key: 'abc', label: 'ABC / XYZ', children: <Matrix data={data} /> },
          ]} />
        )}
      </QueryState>
    </>
  );
}
