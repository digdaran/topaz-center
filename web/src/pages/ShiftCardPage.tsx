import { ArrowLeftOutlined } from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { Button, Card, Col, Descriptions, Row, Table, Tabs, Tag, Typography } from 'antd';
import { Link, useLocation, useParams } from 'react-router-dom';
import { api } from '../api/client';
import { KpiCard, numCol, QueryState } from '../components/ui';
import { dateTime, dec, duration, int, liters, money, Num, toNum } from '../lib/format';
import { PAY, PayCategory } from '../lib/payments';

type Row = Record<string, any>;

interface Card {
  header: Row & {
    station: string; time_zone: string; session_num: number; user_name: string; operators: string; workplaces: number;
    started_at: string; ended_at: string | null; hours: number;
  };
  workplaces: Row[]; tanks: Row[]; hoses: Row[]; fuel: Row[]; cash: Row[]; items: Row[]; rests: Row[]; orders: Row[]; receipts: Row[];
}

const sum = (rows: Row[], key: string) => rows.reduce((a, r) => a + (Number(r[key]) || 0), 0);

/** «Цена»: what the customer paid per litre (Topaz PricePump); the list price crossed out when a discount lowered it. */
const paidPriceCol = () => numCol<Row>('Цена', 'price_paid', (v) => money(v, true), {
  render: (v: Num, r: Row) => {
    const paid = toNum(v), list = toNum(r.price);
    return (
      <span style={{ fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap' }}>
        {money(v, true)}
        {paid != null && list != null && list > paid && (
          <Typography.Text type="secondary" delete style={{ marginLeft: 6, fontSize: 12 }} title="Цена без скидки">{money(list, true)}</Typography.Text>
        )}
      </span>
    );
  },
});

/** The Topaz shift report, with the same sections and numbers (reconciled with a real Topaz export). */
export default function ShiftCardPage() {
  const { station, session } = useParams();
  const location = useLocation();
  const { data, error, isLoading } = useQuery({ queryKey: ['shift', station, session], queryFn: () => api<Card>(`/shifts/${station}/${session}`) });
  const h = data?.header;
  const tz = h?.time_zone;
  const places = data?.workplaces ?? [];
  const severalPlaces = new Set(places.map((w) => w.workbench_id)).size > 1;

  return (
    <QueryState error={error} loading={isLoading}>
      {data && h && (
        <>
          <Link to={`/shifts${location.search}`}><Button type="link" icon={<ArrowLeftOutlined />} style={{ paddingLeft: 0 }}>Все смены</Button></Link>
          <Typography.Title level={3} style={{ marginTop: 0 }}>
            Смена №{h.session_num} · {h.station} {h.ended_at ? null : <Tag color="green">открыта</Tag>}
          </Typography.Title>
          <Descriptions size="small" column={{ xs: 1, sm: 2, lg: 4 }} style={{ marginBottom: 12 }} items={[
            { key: 'u', label: severalPlaces || (h.operators ?? '').includes(',') ? 'Операторы' : 'Оператор', children: h.operators || h.user_name },
            { key: 's', label: 'Начало', children: dateTime(h.started_at, tz) },
            { key: 'e', label: 'Конец', children: h.ended_at ? dateTime(h.ended_at, tz) : '—' },
            { key: 'd', label: 'Длительность', children: duration(h.hours) },
          ]} />
          <Row gutter={[12, 12]} style={{ marginBottom: 12 }}>
            {[
              { title: 'Отпущено топлива', value: h.fuel_volume, format: liters },
              { title: 'Выручка топливо', value: h.fuel_amount, format: money },
              { title: 'Наличные', value: h.pay_cash, format: money },
              { title: 'Банковские карты', value: h.pay_card, format: money },
              ...(Number(h.pay_contract ?? 0) > 0 ? [{ title: 'По договорам', value: h.pay_contract, format: money }] : []),
              ...(Number(h.pay_fuel_card ?? 0) > 0 ? [{ title: 'Топливные карты', value: h.pay_fuel_card, format: money }] : []),
              { title: 'Магазин', value: h.shop_amount, format: money },
              { title: 'Счётчики ТРК', value: h.hose_counters_delta, format: liters },
            ].map((k) => <Col key={k.title} xs={12} md={8} xl={4}><KpiCard {...k} /></Col>)}
          </Row>
          {severalPlaces && (
            <Card size="small" title="Рабочие места" style={{ marginBottom: 12 }}>
              <Table size="small" rowKey="local_session_id" pagination={false} dataSource={places} scroll={{ x: 900 }}
                columns={[
                  {
                    title: 'Место', dataIndex: 'workplace',
                    render: (v: string, r) => <>{v === 'основное' ? 'Основное' : 'Дополнительное'} {r.computer_name && <Typography.Text type="secondary">({r.computer_name})</Typography.Text>}</>,
                  },
                  { title: 'Оператор', dataIndex: 'operator' },
                  { title: 'Начало', dataIndex: 'started_at', render: (v: string) => dateTime(v, tz) },
                  { title: 'Конец', dataIndex: 'ended_at', render: (v: string | null) => (v ? dateTime(v, tz) : <Tag color="green">открыто</Tag>) },
                  numCol<Row>('Литры', 'liters', liters),
                  numCol<Row>('Топливо', 'revenue', money),
                  numCol<Row>('Наличные', 'pay_cash', money),
                  numCol<Row>('Карты', 'pay_card', money),
                  numCol<Row>('Договоры', 'pay_contract', money),
                  numCol<Row>('Заказы', 'orders', int),
                  numCol<Row>('Прерв.', 'aborted', int),
                  numCol<Row>('Магазин', 'shop_amount', money),
                ]} />
            </Card>
          )}
          <Tabs items={[
            {
              key: 'fuel', label: 'Сводка по топливу', children: (
                <Table size="small" rowKey={(_, i) => String(i)} pagination={false} scroll={{ x: 900 }} dataSource={data.fuel}
                  summary={(rows) => (
                    <Table.Summary.Row>
                      <Table.Summary.Cell index={0} colSpan={4}><b>Итого</b></Table.Summary.Cell>
                      <Table.Summary.Cell index={4} align="right"><b>{liters(sum([...rows], 'volume'))}</b></Table.Summary.Cell>
                      <Table.Summary.Cell index={5} align="right"><b>{money(sum([...rows], 'amount'), true)}</b></Table.Summary.Cell>
                      <Table.Summary.Cell index={6} align="right"><b>{int(sum([...rows], 'order_count'))}</b></Table.Summary.Cell>
                      <Table.Summary.Cell index={7} colSpan={3} />
                    </Table.Summary.Row>
                  )}
                  columns={[
                    { title: 'Топливо', dataIndex: 'fuel_name' },
                    { title: 'Резервуар / рукав', render: (_, r) => `${r.tank_num ?? '—'} / ${r.hose_num ?? '—'}` },
                    { title: 'Вид оплаты', dataIndex: 'payment_mode' },
                    { title: 'Пометка', dataIndex: 'remark', render: (v: string) => (v ? <Tag>{v}</Tag> : null) },
                    numCol<Row>('Литры', 'volume', liters),
                    numCol<Row>('Сумма', 'amount', (v) => money(v, true)),
                    numCol<Row>('Заказов', 'order_count', int),
                    paidPriceCol(),
                    numCol<Row>('Скидки', 'discount', (v) => money(v, true)),
                    numCol<Row>('Предоплата нал.', 'amount_cash', (v) => money(v, true)),
                  ]} />
              ),
            },
            {
              key: 'orders', label: `Отпуски (${data.orders.length})`, children: (
                <Table size="small" rowKey="order_id" dataSource={data.orders} pagination={{ pageSize: 50 }} scroll={{ x: 1100 }}
                  columns={[
                    { title: '№', dataIndex: 'order_num', width: 60 },
                    { title: 'Время', dataIndex: 'local_ts', render: (v: string) => v?.replace('T', ' ').slice(0, 19) },
                    { title: 'Топливо', dataIndex: 'fuel_name' },
                    { title: 'Рукав', dataIndex: 'hose_num' },
                    { title: 'Оплата', dataIndex: 'payment_mode', render: (v: string, r) => <>{v} {r.remark && <Tag>{r.remark}</Tag>}</> },
                    { title: 'Способ', dataIndex: 'pay_category', render: (v: PayCategory) => <Tag color={PAY[v]?.color}>{PAY[v]?.short ?? v}</Tag> },
                    numCol<Row>('Литры', 'volume', liters),
                    numCol<Row>('Заказано, л', 'volume_ordered', liters),
                    paidPriceCol(),
                    numCol<Row>('Сумма', 'amount', (v) => money(v, true)),
                    numCol<Row>('Скидка', 'discount', (v) => money(v, true)),
                    { title: 'Карта', dataIndex: 'card_code' },
                    ...(severalPlaces ? [{
                      title: 'Место', dataIndex: 'workplace',
                      render: (v: string | null, r: Row) => (v ? <>{v === 'основное' ? 'осн.' : 'доп.'} · {r.operator}</> : null),
                    }] : []),
                  ]} />
              ),
            },
            {
              key: 'tanks', label: 'Резервуары', children: (
                <Table size="small" rowKey="tank_id" pagination={false} dataSource={data.tanks} scroll={{ x: 900 }}
                  columns={[
                    { title: '№', dataIndex: 'tank_num', width: 50 },
                    { title: 'Топливо', dataIndex: 'fuel_name' },
                    numCol<Row>('Начало', 'start_volume', liters),
                    numCol<Row>('Приход', 'receipt_volume', liters),
                    numCol<Row>('Отпуск', 'pump_volume', liters),
                    numCol<Row>('Конец по учёту', 'book_end_volume', liters),
                    numCol<Row>('Факт (замер)', 'fact_volume', liters),
                    numCol<Row>('Вместимость', 'max_volume', liters),
                    numCol<Row>('Плотность', 'density', (v) => dec(v, 4)),
                    numCol<Row>('Темп., °C', 'temperature', (v) => dec(v, 1)),
                  ]} />
              ),
            },
            {
              key: 'hoses', label: 'Счётчики ТРК', children: (
                <Table size="small" rowKey="hose_id" pagination={false} dataSource={data.hoses}
                  columns={[
                    { title: 'ТРК', dataIndex: 'pump_num' },
                    { title: 'Рукав', dataIndex: 'num_in_pump' },
                    { title: 'Топливо', dataIndex: 'fuel_name' },
                    numCol<Row>('Начало', 'start_counter', (v) => dec(v)),
                    numCol<Row>('Конец', 'end_counter', (v) => dec(v)),
                    numCol<Row>('Разница', 'delta', liters),
                  ]} />
              ),
            },
            {
              key: 'cash', label: 'Касса', children: (
                <Table size="small" rowKey={(_, i) => String(i)} pagination={false} dataSource={data.cash}
                  summary={(rows) => (
                    <Table.Summary.Row>
                      <Table.Summary.Cell index={0} colSpan={3}><b>Остаток (сумма движений)</b></Table.Summary.Cell>
                      <Table.Summary.Cell index={3} align="right"><b>{money(sum([...rows], 'amount'), true)}</b></Table.Summary.Cell>
                    </Table.Summary.Row>
                  )}
                  columns={[
                    { title: 'Операция', dataIndex: 'doc_type' },
                    { title: 'Касса', dataIndex: 'cash_name', render: (v: string) => v?.trim() },
                    numCol<Row>('Кол-во', 'operations', int),
                    numCol<Row>('Сумма', 'amount', (v) => money(v, true)),
                  ]} />
              ),
            },
            {
              key: 'items', label: 'Товары', children: (
                <Row gutter={[12, 12]}>
                  <Col xs={24} xl={14}>
                    <Card size="small" title="Продажи">
                      <Table size="small" rowKey={(_, i) => String(i)} pagination={false} dataSource={data.items}
                        columns={[
                          { title: 'Товар', dataIndex: 'item_name' },
                          { title: 'Оплата', dataIndex: 'payment_mode' },
                          { title: '', dataIndex: 'is_return', render: (v: boolean) => (v ? <Tag color="red">возврат</Tag> : null) },
                          numCol<Row>('Кол-во', 'quantity', (v) => dec(v, 0)),
                          numCol<Row>('Цена', 'price', (v) => money(v, true)),
                          numCol<Row>('Сумма', 'amount', (v) => money(v, true)),
                        ]} />
                    </Card>
                  </Col>
                  <Col xs={24} xl={10}>
                    <Card size="small" title={h.ended_at ? 'Остатки на конец смены' : 'Остатки сейчас'}>
                      <Table size="small" rowKey="item_id" pagination={{ pageSize: 20 }} dataSource={data.rests}
                        columns={[{ title: 'Товар', dataIndex: 'item_name' }, numCol<Row>('Остаток', 'quantity', (v) => dec(v, 0))]} />
                    </Card>
                  </Col>
                </Row>
              ),
            },
            ...(data.receipts.length ? [{
              key: 'rec', label: 'Приём топлива', children: (
                <Table size="small" rowKey="receipt_id" pagination={false} dataSource={data.receipts}
                  columns={[
                    { title: 'Время', dataIndex: 'received_at', render: (v: string) => dateTime(v, tz) },
                    { title: 'Резервуар', dataIndex: 'tank_num' },
                    { title: 'Топливо', dataIndex: 'fuel_name' },
                    { title: 'Документ', dataIndex: 'doc_number' },
                    { title: 'Машина', dataIndex: 'car_number' },
                    numCol<Row>('По документу', 'doc_volume', liters),
                    numCol<Row>('Факт', 'measured_volume', liters),
                    numCol<Row>('Расхождение', 'discrepancy', liters),
                  ]} />
              ),
            }] : []),
          ]} />
        </>
      )}
    </QueryState>
  );
}
