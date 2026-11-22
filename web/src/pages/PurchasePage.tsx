import { DownloadOutlined, PrinterOutlined, SettingOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, App, Button, Card, Checkbox, Col, Drawer, Flex, Form, Input, InputNumber, Modal, Popconfirm, Radio, Row, Segmented, Statistic, Table, Tabs, Tag, Tooltip, Typography } from 'antd';
import { useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { api, csvUrl, useFilter, useFiltered, useMeta } from '../api/client';
import { useMe } from '../auth/auth';
import { FilterBar } from '../components/FilterBar';
import { numCol, QueryState } from '../components/ui';
import { dateTime, dec, int, money } from '../lib/format';
import { MODE, PurchaseLine, STATUS, WEEKDAYS, dayLabel, lineKey, sumAmount } from '../lib/purchasing';

interface Plan {
  settings: { trip_weekdays: number[]; lead_days: number; service_a: number; service_b: number; service_c: number; history_days: number };
  today: string; next_trip: string; following_trip: string; horizon_days: number;
  lines: PurchaseLine[];
  totals: { items: number; amount: number; unpriced: number; out_of_stock: number; core: number; core_candidates: number };
  stations: { station_id: string; station: string; items: number; amount: number }[];
}

interface ListRow {
  id: number; trip_date: string; status: 'draft' | 'bought' | 'closed'; note: string | null; created_by: string; created_at: string;
  bought_at: string | null; items: number; amount: number; unpriced: number; qty: number; received_qty: number; open_items: number;
}

const LIST_STATUS: Record<ListRow['status'], { label: string; color: string }> = {
  draft: { label: 'черновик', color: 'default' },
  bought: { label: 'куплено, ждём прихода', color: 'blue' },
  closed: { label: 'закрыт', color: 'green' },
};

/** The owner's plan for one item: always in stock / do not buy / by demand, minimum, maximum, pack. */
function ItemPlanModal({ line, onClose }: { line: PurchaseLine | null; onClose: () => void }) {
  const [form] = Form.useForm();
  const qc = useQueryClient();
  const { message } = App.useApp();
  useEffect(() => {
    if (line) form.setFieldsValue({ mode: line.mode, min_qty: line.min_qty, max_qty: line.max_qty, pack_qty: line.pack_learned ? null : line.pack === 1 ? null : line.pack, note: line.note });
  }, [line, form]);
  const save = async () => {
    try {
      const v = await form.validateFields();
      await api('/purchasing/items', { method: 'PUT', body: { station_id: line!.station_id, item_key: line!.item_key, ...v } });
      message.success('Сохранено');
      qc.invalidateQueries({ queryKey: ['/purchasing/plan'] });
      onClose();
    } catch (e) {
      if (e instanceof Error) message.error(e.message);
    }
  };
  return (
    <Modal open={!!line} title={`${line?.name ?? ''} · ${line?.station ?? ''}`} onOk={save} onCancel={onClose} okText="Сохранить" destroyOnClose>
      <Form form={form} layout="vertical">
        <Form.Item name="mode" label="Как закупать">
          <Radio.Group optionType="button" options={(['auto', 'core', 'stop'] as const).map((k) => ({ value: k, label: MODE[k].label }))} />
        </Form.Item>
        <Typography.Paragraph type="secondary" style={{ marginTop: -8 }}>
          «По продажам» — сколько нужно до следующей поездки. «Всегда в наличии» — хотя бы одна упаковка на полке, даже если товар давно не продавался.
          «Не закупать» — товар выводим: остаток распродаём, в список он не попадает.
        </Typography.Paragraph>
        <Row gutter={12}>
          <Col span={8}><Form.Item name="min_qty" label="Минимум на полке"><InputNumber min={0} style={{ width: '100%' }} /></Form.Item></Col>
          <Col span={8}><Form.Item name="max_qty" label="Максимум"><InputNumber min={0.001} style={{ width: '100%' }} /></Form.Item></Col>
          <Col span={8}>
            <Form.Item name="pack_qty" label="Упаковка" extra={line?.pack_learned ? `по прошлым приходам: ${dec(line.pack, 0)}` : undefined}>
              <InputNumber min={0.001} style={{ width: '100%' }} placeholder={line ? String(line.pack).replace('.', ',') : ''} />
            </Form.Item>
          </Col>
        </Row>
        <Form.Item name="note" label="Заметка"><Input maxLength={500} placeholder="например: брать только в «Метро»" /></Form.Item>
      </Form>
    </Modal>
  );
}

/** Trip days, days from the purchase to the shelf, how surely each class must not run out, how much history counts. */
function SettingsModal({ open, plan, onClose }: { open: boolean; plan: Plan; onClose: () => void }) {
  const [form] = Form.useForm();
  const qc = useQueryClient();
  const { message } = App.useApp();
  useEffect(() => { if (open) form.setFieldsValue(plan.settings); }, [open, plan, form]);
  const save = async () => {
    try {
      const v = await form.validateFields();
      await api('/purchasing/settings', { method: 'PUT', body: v });
      message.success('Настройки сохранены');
      qc.invalidateQueries({ queryKey: ['/purchasing/plan'] });
      onClose();
    } catch (e) {
      if (e instanceof Error) message.error(e.message);
    }
  };
  return (
    <Modal open={open} title="Поездки за товаром" onOk={save} onCancel={onClose} okText="Сохранить" destroyOnClose width={560}>
      <Form form={form} layout="vertical">
        <Form.Item name="trip_weekdays" label="В какие дни вы ездите за товаром" rules={[{ required: true, message: 'Выберите хотя бы один день' }]}>
          <Checkbox.Group options={WEEKDAYS.map((d, i) => ({ value: i + 1, label: d }))} />
        </Form.Item>
        <Form.Item name="lead_days" label="Сколько дней от покупки до полки" extra="0 — товар на полке в день поездки">
          <InputNumber min={0} max={14} style={{ width: 120 }} />
        </Form.Item>
        <Typography.Text strong>Насколько надёжно товар не должен кончаться до следующей поездки</Typography.Text>
        <Row gutter={12} style={{ marginTop: 8 }}>
          <Col span={8}><Form.Item name="service_a" label="Главные (A)" extra="80 % выручки"><InputNumber min={50} max={99.9} addonAfter="%" /></Form.Item></Col>
          <Col span={8}><Form.Item name="service_b" label="Средние (B)"><InputNumber min={50} max={99.9} addonAfter="%" /></Form.Item></Col>
          <Col span={8}><Form.Item name="service_c" label="Остальные (C)"><InputNumber min={50} max={99.9} addonAfter="%" /></Form.Item></Col>
        </Row>
        <Form.Item name="history_days" label="По продажам за сколько последних дней считать" extra="Дни, когда товара не было, не считаются">
          <InputNumber min={14} max={180} style={{ width: 120 }} />
        </Form.Item>
      </Form>
    </Modal>
  );
}

function BuyTable({ lines, qty, setQty, many, onEdit }: {
  lines: PurchaseLine[]; qty: Record<string, number>; setQty: (k: string, v: number) => void; many: boolean; onEdit: (l: PurchaseLine) => void;
}) {
  return (
    <Table size="small" rowKey={lineKey} dataSource={lines} pagination={false} scroll={{ x: 'max-content' }}
      locale={{ emptyText: 'Покупать ничего не нужно' }}
      columns={[
        // the station and group under the name (sorted by group, like the rows of the store): narrow screens keep "Купить" in view
        {
          title: 'Товар', dataIndex: 'name', render: (v: string, r) => (
            <><a onClick={() => onEdit(r)}>{v}</a><div><Typography.Text type="secondary" style={{ fontSize: 12 }}>{many ? <b>{r.station}</b> : null}{many ? ' · ' : ''}{r.group_name ?? 'Без группы'}</Typography.Text></div></>
          ),
        },
        {
          title: 'Купить', key: 'qty', align: 'right' as const,
          render: (_: unknown, r: PurchaseLine) => (
            <InputNumber size="small" min={0} step={r.pack} value={qty[lineKey(r)] ?? r.qty} style={{ width: 90 }}
              onChange={(v) => setQty(lineKey(r), Number(v ?? 0))} />
          ),
        },
        { title: '', dataIndex: 'status', render: (v: string, r: PurchaseLine) => <><Tag color={STATUS[v]?.color}>{STATUS[v]?.label}</Tag>{r.mode !== 'auto' && <Tag color={MODE[r.mode].color}>{MODE[r.mode].short}</Tag>}</> },
        numCol<PurchaseLine>('Остаток', 'stock', (v) => dec(v, 0)),
        numCol<PurchaseLine>('Продаётся в день', 'per_day', (v) => dec(v, 2), {
          title: <Tooltip title="Только по дням, когда товар был в наличии">Продаётся в день</Tooltip>,
        }),
        numCol<PurchaseLine>('Упаковка', 'pack', (v) => String(v).replace('.', ',')),
        numCol<PurchaseLine>('Цена', 'price', (v) => money(v, true)),
        {
          title: 'Сумма', key: 'amount', align: 'right' as const,
          render: (_: unknown, r: PurchaseLine) => (r.price != null ? money(r.price * (qty[lineKey(r)] ?? r.qty), true) : <Tooltip title="Нет закупочной цены: ни прихода в Топазе, ни цены в «Себестоимости»">—</Tooltip>),
        },
      ]} />
  );
}

function AllItems({ lines, many, onEdit }: { lines: PurchaseLine[]; many: boolean; onEdit: (l: PurchaseLine) => void }) {
  const [mode, setMode] = useState<string>('all');
  const rows = lines.filter((l) => mode === 'all' || (mode === 'candidates' ? l.abc === 'A' && l.mode === 'auto' : l.mode === mode));
  const count = (m: string) => lines.filter((l) => (m === 'candidates' ? l.abc === 'A' && l.mode === 'auto' : l.mode === m)).length;
  return (
    <>
      <Segmented<string> style={{ marginBottom: 12 }} value={mode} onChange={setMode} options={[
        { value: 'all', label: `Все (${lines.length})` },
        { value: 'core', label: `Всегда в наличии (${count('core')})` },
        { value: 'stop', label: `Не закупать (${count('stop')})` },
        { value: 'candidates', label: <Tooltip title="Товары группы A — дают 80 % выручки — но ещё не отмечены «Всегда в наличии»">Кандидаты в «всегда в наличии» ({count('candidates')})</Tooltip> },
      ]} />
      <Table size="small" rowKey={lineKey} dataSource={rows} pagination={{ pageSize: 50 }} scroll={{ x: 'max-content' }}
        columns={[
          ...(many ? [{ title: 'АЗС', dataIndex: 'station' }] : []),
          { title: 'Товар', dataIndex: 'name', render: (v: string, r: PurchaseLine) => <a onClick={() => onEdit(r)}>{v}</a> },
          { title: 'Группа', dataIndex: 'group_name' },
          { title: 'ABC', dataIndex: 'abc', render: (v: string) => <Tag color={v === 'A' ? 'green' : v === 'B' ? 'gold' : 'default'}>{v}</Tag> },
          { title: 'Как закупать', dataIndex: 'mode', render: (v: PurchaseLine['mode']) => <Tag color={MODE[v].color}>{MODE[v].label}</Tag> },
          numCol<PurchaseLine>('Остаток', 'stock', (v) => dec(v, 0)),
          numCol<PurchaseLine>('В пути', 'on_order', (v) => (Number(v) > 0 ? dec(v, 0) : '—')),
          numCol<PurchaseLine>('Продаётся в день', 'per_day', (v) => dec(v, 2)),
          numCol<PurchaseLine>('Хватит на, дней', 'days_of_cover', (v) => dec(v, 1)),
          {
            ...numCol<PurchaseLine>('Дней без товара', 'zero_days', int),
            title: <Tooltip title="Сколько дней из расчётного периода товара не было на полке">Дней без товара</Tooltip>,
          },
          { title: 'Мин / макс', key: 'mm', render: (_: unknown, r: PurchaseLine) => (r.min_qty != null || r.max_qty != null ? `${r.min_qty ?? '—'} / ${r.max_qty ?? '—'}` : '') },
          { title: 'Заметка', dataIndex: 'note' },
          { title: 'Последняя продажа', dataIndex: 'last_sale', render: (v: string | null) => v?.split('-').reverse().join('.') ?? '—' },
        ]} />
    </>
  );
}

/** A saved list: its lines with what Topaz has received; a draft can be bought or deleted, a bought one closed. */
function ListDrawer({ id, onClose }: { id: number | null; onClose: () => void }) {
  const { data } = useQuery({
    queryKey: ['/purchasing/lists', id], enabled: id != null,
    queryFn: () => api<{ list: ListRow; lines: (PurchaseLine & { received_qty: number | null; suggested_qty: number })[] }>(`/purchasing/lists/${id}`),
  });
  const many = new Set(data?.lines.map((l) => l.station_id)).size > 1;
  return (
    <Drawer open={id != null} onClose={onClose} width={860} title={data ? `Список на ${dayLabel(data.list.trip_date)}` : 'Список'}
      extra={id != null && <Button icon={<DownloadOutlined />} href={`/api/v1/ui/purchasing/lists/${id}?format=csv`}>Excel</Button>}>
      {data && (
        <Table size="small" rowKey={lineKey} dataSource={data.lines.filter((l) => l.qty > 0)} pagination={false} scroll={{ x: 'max-content' }}
          columns={[
            { title: 'Группа', dataIndex: 'group_name' },
            { title: 'Товар', dataIndex: 'name' },
            ...(many ? [{ title: 'АЗС', dataIndex: 'station' }] : []),
            numCol('Куплено', 'qty', (v) => dec(v, 3)),
            {
              title: 'Пришло в Топаз', key: 'r', align: 'right' as const,
              render: (_: unknown, r) => (data.list.status === 'draft' ? '—'
                : (r.received_qty ?? 0) >= r.qty ? <Tag color="green">{dec(r.received_qty, 3)}</Tag>
                : <Tag color="orange">{dec(r.received_qty ?? 0, 3)}</Tag>),
            },
            numCol('Цена', 'price', (v) => money(v, true)),
            numCol('Сумма', 'amount', (v) => money(v, true)),
          ]} />
      )}
    </Drawer>
  );
}

function Lists() {
  const qc = useQueryClient();
  const { message } = App.useApp();
  const [open, setOpen] = useState<number | null>(null);
  const { data, error, isLoading } = useQuery({ queryKey: ['/purchasing/lists'], queryFn: () => api<ListRow[]>('/purchasing/lists') });
  const act = async (path: string, method: string, done: string) => {
    try {
      await api(path, { method });
      message.success(done);
      qc.invalidateQueries({ queryKey: ['/purchasing/lists'] });
      qc.invalidateQueries({ queryKey: ['/purchasing/plan'] });
    } catch (e) {
      message.error((e as Error).message);
    }
  };
  return (
    <QueryState error={error} loading={isLoading}>
      <Table size="small" rowKey="id" dataSource={data ?? []} pagination={{ pageSize: 20 }} scroll={{ x: 'max-content' }}
        locale={{ emptyText: 'Сохранённых списков ещё нет' }}
        columns={[
          { title: 'Поездка', dataIndex: 'trip_date', render: (v: string, r) => <a onClick={() => setOpen(r.id)}>{dayLabel(v)}</a> },
          { title: 'Состояние', dataIndex: 'status', render: (v: ListRow['status'], r) => <><Tag color={LIST_STATUS[v].color}>{LIST_STATUS[v].label}</Tag>{v === 'bought' && r.open_items > 0 && <Typography.Text type="secondary">не пришло: {int(r.open_items)} поз.</Typography.Text>}</> },
          numCol<ListRow>('Позиций', 'items', int),
          numCol<ListRow>('Сумма', 'amount', (v) => money(v, true)),
          { title: 'Составил', dataIndex: 'created_by' },
          { title: 'Создан', dataIndex: 'created_at', render: (v: string) => dateTime(v) },
          {
            title: '', key: 'a', render: (_: unknown, r: ListRow) => (
              <Flex gap={8}>
                {r.status === 'draft' && <Popconfirm title="Отметить список купленным?" description="Товары будут числиться «в пути», пока не появятся в Топазе." onConfirm={() => act(`/purchasing/lists/${r.id}/bought`, 'POST', 'Отмечено: куплено')}><Button size="small" type="primary">Куплено</Button></Popconfirm>}
                {r.status === 'draft' && <Popconfirm title="Удалить черновик?" onConfirm={() => act(`/purchasing/lists/${r.id}`, 'DELETE', 'Удалено')}><Button size="small" danger>Удалить</Button></Popconfirm>}
                {r.status === 'bought' && <Popconfirm title="Больше не ждать прихода?" description="Недоставленное снова попадёт в список к покупке." onConfirm={() => act(`/purchasing/lists/${r.id}/close`, 'POST', 'Список закрыт')}><Button size="small">Закрыть</Button></Popconfirm>}
              </Flex>
            ),
          },
        ]} />
      <ListDrawer id={open} onClose={() => setOpen(null)} />
    </QueryState>
  );
}

/** Склад → Закупка: what to buy on the next trip, for all stations at once. */
export default function PurchasePage() {
  const me = useMe();
  const meta = useMeta();
  const [filter] = useFilter();
  const qc = useQueryClient();
  const { message } = App.useApp();
  const { data, error, isLoading } = useFiltered<Plan>('/purchasing/plan');
  const [qty, setQtyMap] = useState<Record<string, number>>({});
  const [edit, setEdit] = useState<PurchaseLine | null>(null);
  const [settings, setSettings] = useState(false);
  const [saving, setSaving] = useState(false);
  const [params] = useSearchParams();
  const [tab, setTab] = useState(params.get('tab') ?? 'buy');
  const setQty = (k: string, v: number) => setQtyMap((m) => ({ ...m, [k]: v }));
  useEffect(() => setQtyMap({}), [data?.next_trip, data?.lines.length]);

  const buy = useMemo(() => (data?.lines ?? []).filter((l) => l.qty > 0 || (qty[lineKey(l)] ?? 0) > 0), [data, qty]);
  const amount = sumAmount(buy, qty);
  const many = (meta.data?.stations.length ?? 0) > 1;

  const saveList = async () => {
    if (!data) return;
    setSaving(true);
    try {
      await api('/purchasing/lists', {
        method: 'POST',
        body: {
          trip_date: data.next_trip,
          lines: buy.map((l) => ({ station_id: l.station_id, item_id: l.item_id, item_key: l.item_key, name: l.name, group_name: l.group_name, suggested_qty: l.qty, qty: qty[lineKey(l)] ?? l.qty, price: l.price })),
        },
      });
      message.success('Список сохранён — он во вкладке «Списки». После покупки отметьте «Куплено».');
      qc.invalidateQueries({ queryKey: ['/purchasing/lists'] });
      setTab('lists');
    } catch (e) {
      message.error((e as Error).message);
    } finally {
      setSaving(false);
    }
  };

  return (
    <>
      <FilterBar granularity={false} compare={false} period={false} />
      <QueryState error={error} loading={isLoading}>
        {data && (
          <>
            <Card size="small" style={{ marginBottom: 12 }}>
              <Row gutter={[24, 12]} align="middle">
                <Col xs={12} md={5}><Statistic title="Ближайшая поездка" value={dayLabel(data.next_trip)} valueStyle={{ fontSize: 20 }} /></Col>
                <Col xs={12} md={5}>
                  <Statistic title={<Tooltip title={`Чтобы хватило до следующей поездки (${dayLabel(data.following_trip)})${data.settings.lead_days ? ` и ещё ${data.settings.lead_days} дн. до полки` : ''}`}>Закупаем на, дней</Tooltip>} value={data.horizon_days} valueStyle={{ fontSize: 20 }} />
                </Col>
                <Col xs={12} md={4}><Statistic title="Позиций" value={buy.length} valueStyle={{ fontSize: 20 }} /></Col>
                <Col xs={12} md={5}><Statistic title="Бюджет поездки" value={amount === 0 && buy.length > 0 && buy.every((l) => l.price == null) ? '—' : money(amount, true)} valueStyle={{ fontSize: 20 }} /></Col>
                <Col xs={24} md={5}>
                  <Flex gap={8} wrap justify="flex-end">
                    {(me.role === 'owner' || me.role === 'superadmin') && <Button icon={<SettingOutlined />} onClick={() => setSettings(true)}>Поездки</Button>}
                    <Button icon={<DownloadOutlined />} href={csvUrl('/purchasing/plan', filter)}>Excel</Button>
                    <Button icon={<PrinterOutlined />} onClick={() => { setTab('buy'); setTimeout(() => window.print(), 100); }}>Печать</Button>
                  </Flex>
                </Col>
              </Row>
              {data.totals.out_of_stock > 0 && (
                <Alert type="warning" showIcon style={{ marginTop: 12 }}
                  message={`Сейчас нет на полке: ${int(data.totals.out_of_stock)} ходовых товаров`}
                  description="Они вверху списка. Спрос по ним посчитан только по дням, когда товар был, — поэтому сервис предлагает купить больше, чем раньше подсказывал раздел «Товары»." />
              )}
              {data.totals.unpriced > 0 && (
                <Typography.Paragraph type="secondary" style={{ margin: '8px 0 0' }}>
                  У {int(data.totals.unpriced)} позиций нет закупочной цены — они не вошли в бюджет. Цены берутся из приходов в Топазе или из «Деньги → Себестоимость».
                </Typography.Paragraph>
              )}
            </Card>
            <Tabs activeKey={tab} onChange={setTab} items={[
              {
                key: 'buy', label: `К покупке (${buy.length})`, children: (
                  <>
                    <BuyTable lines={buy} qty={qty} setQty={setQty} many={many} onEdit={setEdit} />
                    <Flex justify="flex-end" gap={8} style={{ marginTop: 12 }}>
                      <Button type="primary" loading={saving} disabled={!buy.length} onClick={saveList}>Сохранить список</Button>
                    </Flex>
                  </>
                ),
              },
              { key: 'all', label: 'Все товары', children: <AllItems lines={data.lines} many={many} onEdit={setEdit} /> },
              { key: 'lists', label: 'Списки', children: <Lists /> },
            ]} />
            <ItemPlanModal line={edit} onClose={() => setEdit(null)} />
            <SettingsModal open={settings} plan={data} onClose={() => setSettings(false)} />
          </>
        )}
      </QueryState>
    </>
  );
}
