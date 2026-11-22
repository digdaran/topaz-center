import { Alert, Card, Col, Row, Table, Tag, Typography } from 'antd';
import { Link } from 'react-router-dom';
import { useFiltered, useMeta } from '../api/client';
import { KpiCard, numCol } from '../components/ui';
import { dec, int, money, pct } from '../lib/format';

interface StationFigures {
  station_id: string; station: string; core_items: number; core_marked: boolean; availability: number | null;
  core_out_now: number; out_items: number; lost_revenue: number;
}
interface LostItem {
  station_id: string; station: string; item_key: string; name: string; group_name: string | null; core: boolean; stock: number;
  period_days: number; out_days: number; per_day: number; price: number | null; lost_qty: number; lost_revenue: number | null;
}
export interface AvailabilityData {
  total: { availability: number | null; core_items: number; core_marked: boolean; core_out_now: number; lost_revenue: number; out_items: number };
  stations: StationFigures[];
  items: LostItem[];
}

const CORE_HINT = 'Доля дней за период, когда товары ядра были на полке. Ядро — товары, отмеченные «Всегда в наличии» в «Склад → Закупка»; '
  + 'пока ничего не отмечено — товары, дающие 80 % выручки. Сегодняшний день не считается: он ещё не закончился.';
const LOST_HINT = 'Сколько товары не продали за дни без остатка: обычные продажи в день (по дням, когда товар был на полке) × средняя цена продажи × дни на нуле. '
  + 'Товары с отметкой «Не закупать» не считаются.';

/** Склад → Товары: core availability and the revenue lost to empty shelves over the filter period. */
export function AvailabilityTiles({ data }: { data: AvailabilityData }) {
  const meta = useMeta();
  const many = (meta.data?.stations.length ?? 0) > 1 && data.stations.length > 1;
  const t = data.total;
  return (
    <>
      <Row gutter={[12, 12]} style={{ marginBottom: 12 }}>
        <Col xs={12} md={6}><KpiCard title="Доступность ядра" value={t.availability} format={pct} hint={CORE_HINT} /></Col>
        <Col xs={12} md={6}><KpiCard title="Упущенная выручка" value={t.lost_revenue} format={money} hint={LOST_HINT} /></Col>
        <Col xs={12} md={6}><KpiCard title="Ядро: нет на полке сейчас" value={t.core_out_now} format={(v) => `${int(v)} из ${int(t.core_items)}`} /></Col>
        <Col xs={12} md={6}><KpiCard title="Товаров кончалось за период" value={t.out_items} format={int} /></Col>
      </Row>
      {!t.core_marked && t.core_items > 0 && (
        <Alert type="info" showIcon style={{ marginBottom: 12 }}
          message={<>Ядро пока посчитано по выручке. Отметьте товары, которые должны быть всегда, в <Link to="/stock/purchase">«Склад → Закупка»</Link> («Всегда в наличии»).</>} />
      )}
      {many && (
        <Table<StationFigures> size="small" style={{ marginBottom: 12 }} rowKey="station_id" pagination={false} dataSource={data.stations} scroll={{ x: 'max-content' }}
          columns={[
            { title: 'АЗС', dataIndex: 'station' },
            { ...numCol<StationFigures>('Доступность ядра', 'availability', pct), render: (v: number | null, r) => (r.core_items ? pct(v) : '—') },
            numCol<StationFigures>('Товаров в ядре', 'core_items', int),
            numCol<StationFigures>('Ядро на нуле сейчас', 'core_out_now', int),
            numCol<StationFigures>('Упущенная выручка', 'lost_revenue', money),
          ]} />
      )}
    </>
  );
}

/** Items that lost the most sales while out of stock. */
export function LostSales({ data }: { data: AvailabilityData }) {
  const meta = useMeta();
  const many = (meta.data?.stations.length ?? 0) > 1;
  return (
    <Card size="small" title={<>Упущенные продажи <Typography.Text type="secondary" style={{ fontWeight: 400 }}>· товары, которых не было на полке, по упущенной выручке</Typography.Text></>}>
      <Table<LostItem> size="small" rowKey={(r) => `${r.station_id}/${r.item_key}`} dataSource={data.items} pagination={{ pageSize: 30 }} scroll={{ x: 'max-content' }}
        locale={{ emptyText: 'За период товары не кончались' }}
        columns={[
          ...(many ? [{ title: 'АЗС', dataIndex: 'station', key: 'st' }] : []),
          { title: 'Товар', dataIndex: 'name', render: (v: string, r) => <>{v} {r.core && <Tag color="green">ядро</Tag>}</> },
          { title: 'Группа', dataIndex: 'group_name' },
          { ...numCol<LostItem>('Дней без остатка', 'out_days', int), render: (v: number, r) => `${int(v)} из ${int(r.period_days)}` },
          numCol<LostItem>('Продаётся в день', 'per_day', (v) => dec(v, 2)),
          numCol<LostItem>('Не продано, шт.', 'lost_qty', (v) => dec(v, 0)),
          numCol<LostItem>('Упущенная выручка', 'lost_revenue', money),
          numCol<LostItem>('Остаток сейчас', 'stock', (v) => dec(v, 0)),
        ]} />
    </Card>
  );
}

export const useAvailability = (enabled: boolean) => useFiltered<AvailabilityData>('/analytics/availability', {}, enabled);
