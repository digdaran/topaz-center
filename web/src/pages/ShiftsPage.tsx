import { Table, Tabs, Tag, Tooltip } from 'antd';
import { useLocation, useNavigate } from 'react-router-dom';
import { useFiltered, useMeta } from '../api/client';
import { FilterBar } from '../components/FilterBar';
import { numCol, PageTitle, QueryState } from '../components/ui';
import { dateTime, dec, duration, int, liters, money, pct } from '../lib/format';

interface ShiftRow {
  station_id: string; station: string; session_id: number; session_num: number; user_name: string; operators: string; workplaces: number;
  started_at: string; ended_at: string | null;
  hours: number; fuel_volume: number | null; fuel_amount: number | null; fuel_cash: number | null; fuel_cashless: number | null;
  pay_cash: number | null; pay_card: number | null; pay_contract: number | null; pay_fuel_card: number | null;
  shop_amount: number | null; hose_counters_delta: number | null; aborted: number; orders: number;
}

interface OperatorRow {
  user_name: string; shifts: number; stations: number; hours: number; revenue: number; liters: number; orders: number; aborted: number;
  service_liters: number; discounts: number; revenue_per_shift: number; aborted_share: number | null;
}

function ShiftList() {
  const { data, error, isLoading } = useFiltered<ShiftRow[]>('/shifts');
  const navigate = useNavigate();
  const location = useLocation();
  const meta = useMeta();
  const tz = (id: string) => meta.data?.stations.find((s) => s.id === id)?.time_zone;
  const many = (meta.data?.stations.length ?? 0) > 1;
  return (
    <QueryState error={error} loading={isLoading}>
      <Table<ShiftRow>
        size="small"
        rowKey={(r) => `${r.station_id}/${r.session_id}`}
        dataSource={data}
        scroll={{ x: 1100 }}
        pagination={{ pageSize: 50, showSizeChanger: false }}
        onRow={(r) => ({ onClick: () => navigate(`/shifts/${r.station_id}/${r.session_id}${location.search}`), style: { cursor: 'pointer' } })}
        columns={[
          ...(many ? [{ title: 'АЗС', dataIndex: 'station', key: 'station', fixed: 'left' as const }] : []),
          { title: '№', dataIndex: 'session_num', key: 'n', width: 60 },
          { title: 'Начало', dataIndex: 'started_at', key: 'start', render: (v: string, r) => dateTime(v, tz(r.station_id)) },
          {
            title: 'Конец', dataIndex: 'ended_at', key: 'end',
            render: (v: string | null, r) => (v ? dateTime(v, tz(r.station_id)) : <Tag color="green">открыта</Tag>),
          },
          { title: 'Длительность', dataIndex: 'hours', key: 'h', render: (v: number) => <Tooltip title={`${dec(v, 1)} ч`}>{duration(v)}</Tooltip> },
          {
            title: 'Оператор', dataIndex: 'operators', key: 'user',
            render: (v: string, r) => (r.workplaces > 1
              ? <Tooltip title={`Продажи шли с ${r.workplaces} рабочих мест`}>{v || r.user_name}</Tooltip>
              : v || r.user_name),
          },
          numCol<ShiftRow>('Литры', 'fuel_volume', liters),
          numCol<ShiftRow>('Топливо', 'fuel_amount', money),
          numCol<ShiftRow>('Наличные', 'pay_cash', money),
          numCol<ShiftRow>('Карты', 'pay_card', money),
          numCol<ShiftRow>('Договоры', 'pay_contract', money),
          numCol<ShiftRow>('Магазин', 'shop_amount', money),
          numCol<ShiftRow>('Заказы', 'orders', int),
          { ...numCol<ShiftRow>('Прерв.', 'aborted', int), render: (v: number) => (v > 0 ? <Tag color="orange">{v}</Tag> : '0') },
          {
            ...numCol<ShiftRow>('Счётчики ТРК', 'hose_counters_delta', liters),
            render: (v: number | null, r) => {
              const diff = v === null || r.fuel_volume === null ? 0 : Math.abs(v - r.fuel_volume);
              return diff > 0.5 ? <Tooltip title="Разница счётчиков ТРК не совпадает с отпуском по заказам"><Tag color="red">{liters(v)}</Tag></Tooltip> : liters(v);
            },
          },
        ]}
      />
    </QueryState>
  );
}

function Operators() {
  const { data, error, isLoading } = useFiltered<OperatorRow[]>('/shifts/operators');
  return (
    <QueryState error={error} loading={isLoading}>
      <Table<OperatorRow>
        size="small"
        rowKey="user_name"
        dataSource={data}
        pagination={false}
        scroll={{ x: 900 }}
        columns={[
          { title: 'Оператор', dataIndex: 'user_name', key: 'u', fixed: 'left' },
          numCol<OperatorRow>('Смен', 'shifts', int),
          numCol<OperatorRow>('Часов', 'hours', (v) => dec(v, 1)),
          numCol<OperatorRow>('Выручка топлива', 'revenue', money, { defaultSortOrder: 'descend' }),
          numCol<OperatorRow>('На смену', 'revenue_per_shift', money),
          numCol<OperatorRow>('Литры', 'liters', liters),
          numCol<OperatorRow>('Заказы', 'orders', int),
          numCol<OperatorRow>('Прерванные', 'aborted', int),
          {
            ...numCol<OperatorRow>('Доля прерванных', 'aborted_share', pct),
            render: (v: number | null) => (v !== null && v > 0.05 ? <Tag color="orange">{pct(v)}</Tag> : pct(v)),
          },
          numCol<OperatorRow>('Тех. прокачки', 'service_liters', liters),
          numCol<OperatorRow>('Скидки', 'discounts', money),
        ]}
      />
    </QueryState>
  );
}

export default function ShiftsPage() {
  return (
    <>
      <PageTitle title="Смены" />
      <FilterBar granularity={false} compare={false} />
      <Tabs items={[
        { key: 'list', label: 'Список смен', children: <ShiftList /> },
        { key: 'ops', label: 'Операторы', children: <Operators /> },
      ]} />
    </>
  );
}
