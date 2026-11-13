import { DeleteOutlined, UploadOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, App, Badge, Button, Card, Checkbox, DatePicker, Form, Input, InputNumber, Modal, Popconfirm, Segmented, Select, Space, Table, Tabs, Tag, Tooltip, Typography } from 'antd';
import dayjs from 'dayjs';
import { useState } from 'react';
import { api, useFilter, useFiltered, useMeta, isOwner } from '../api/client';
import { useMe } from '../auth/auth';
import { FilterBar } from '../components/FilterBar';
import { numCol, QueryState } from '../components/ui';
import { dateTime, liters, money, pct } from '../lib/format';

type R = Record<string, any>;

function FuelCosts() {
  const [filter] = useFilter();
  const qc = useQueryClient();
  const { message } = App.useApp();
  const { data, error, isLoading } = useFiltered<{ receipts: R[]; costs: R[]; fuels: R[]; unpriced_receipts: number; vat: { mode: string; rate: number } }>('/costs/fuel');
  const [add, setAdd] = useState<R | null>(null);
  const [onlyUnpriced, setOnlyUnpriced] = useState(false);
  const vatLabel = data?.vat?.mode === 'net' ? 'без НДС' : 'с НДС';
  const [form] = Form.useForm();

  const submit = async () => {
    const v = await form.validateFields();
    try {
      const r = await api<{ stations: number }>('/costs/fuel', {
        method: 'POST',
        body: { station_id: add!.station_id, fuel_id: add!.fuel_id, receipt_id: add!.receipt_id ?? null, valid_from: v.valid_from.toISOString(), price_per_liter: v.price, comment: v.comment, all_stations_with_same_type: !!v.all },
      });
      message.success(r.stations > 1 ? `Цена записана для ${r.stations} АЗС. Маржа пересчитывается.` : 'Сохранено. Маржа пересчитывается.');
      setAdd(null);
      qc.invalidateQueries();
    } catch (e) {
      message.error((e as Error).message);
    }
  };
  const open = (r: R, from: string | null) => {
    setAdd(r);
    form.setFieldsValue({ valid_from: from ? dayjs(from) : dayjs().startOf('day'), price: r.price_per_liter ?? undefined, comment: r.doc_number ? `ТТН ${r.doc_number}` : undefined, all: false });
  };
  const remove = async (id: number) => {
    await api(`/costs/fuel/${id}`, { method: 'DELETE' });
    qc.invalidateQueries();
  };

  return (
    <QueryState error={error} loading={isLoading}>
      {data && (
        <>
          <Card size="small" title="Текущие цены" style={{ marginBottom: 12 }}>
            <Table size="small" rowKey={(r) => `${r.station_id}/${r.fuel_id}`} pagination={false} dataSource={data.fuels} scroll={{ x: 700 }}
              columns={[
                { title: 'АЗС', dataIndex: 'station' },
                { title: 'Топливо', dataIndex: 'fuel_name' },
                numCol<R>('Себестоимость, ₽/л', 'current_cost', (v) => (v == null ? 'не задана' : money(v, true))),
                numCol<R>('Продажа, ₽/л', 'current_price', (v) => money(v, true)),
                { title: 'Наценка', key: 'm', align: 'right', render: (_, r) => (r.current_cost ? pct((r.current_price - r.current_cost) / r.current_cost) : '—') },
                { title: '', key: 'a', render: (_, r) => <Button size="small" onClick={() => open(r, null)}>Цена остатка на дату</Button> },
              ]} />
            <Typography.Paragraph type="secondary" style={{ margin: '8px 0 0' }}>
              Себестоимость литра — средняя цена топлива в резервуаре: при каждом сливе она пересчитывается с учётом остатка
              (остаток × средняя + литры слива × цена слива) / (остаток + литры). «Цена остатка на дату» задаёт среднюю, например, на начало учёта.
            </Typography.Paragraph>
          </Card>
          <Card size="small"
            title={<Space wrap>Сливы топлива за период — укажите закупочную цену по накладной ({vatLabel})
              {data.unpriced_receipts > 0 && <Tag color="orange">без цены за 30 дней: {data.unpriced_receipts}</Tag>}</Space>}
            extra={<Segmented size="small" value={onlyUnpriced ? 'unpriced' : 'all'} onChange={(v) => setOnlyUnpriced(v === 'unpriced')}
              options={[{ value: 'all', label: 'Все' }, { value: 'unpriced', label: 'Без цены' }]} />}>
            <Table size="small" rowKey={(r) => `${r.station_id}/${r.receipt_id}`} pagination={{ pageSize: 20 }} scroll={{ x: 1000 }}
              dataSource={onlyUnpriced ? data.receipts.filter((r) => r.unpriced) : data.receipts}
              locale={{ emptyText: `Нет приёмов топлива с ${filter.from.split('-').reverse().join('.')} по ${filter.to.split('-').reverse().join('.')}` }}
              columns={[
                { title: 'Когда', dataIndex: 'received_at', render: (v) => dateTime(v) },
                { title: 'АЗС', dataIndex: 'station' },
                { title: 'Топливо', dataIndex: 'fuel_name' },
                { title: 'Резервуар', dataIndex: 'tank_num', render: (v) => v ?? '—' },
                { title: 'Документ', dataIndex: 'doc_number' },
                numCol<R>('Литры', 'doc_volume', liters),
                numCol<R>('Остаток до слива', 'stock_before', (v) => (v == null ? '—' : liters(v))),
                {
                  title: 'Цена слива', key: 'p', align: 'right', render: (_, r) => r.price_per_liter != null
                    ? <b>{money(r.price_per_liter, true)}</b>
                    : <Badge status="warning" text={<Typography.Text type="warning">не задана</Typography.Text>} />,
                },
                {
                  title: <Tooltip title="Средняя цена литра в резервуаре после слива: по ней считаются продажи до следующего слива">Средняя после</Tooltip>,
                  key: 'avg', align: 'right', render: (_, r) => (r.average_after == null ? '—'
                    : r.unpriced ? <Tooltip title="Цены слива нет: литры считаются по прежней средней"><Typography.Text type="secondary">{money(r.average_after, true)}</Typography.Text></Tooltip>
                    : money(r.average_after, true)),
                },
                { title: '', key: 'a', render: (_, r) => <Button size="small" type={r.price_per_liter == null ? 'primary' : 'default'} onClick={() => open(r, r.received_at)}>{r.price_per_liter == null ? 'Указать цену' : 'Изменить'}</Button> },
              ]} />
          </Card>
          <Card size="small" title="История цен" style={{ marginTop: 12 }}>
            <Table size="small" rowKey="id" pagination={{ pageSize: 20 }} dataSource={data.costs} scroll={{ x: 800 }}
              columns={[
                { title: 'Действует с', dataIndex: 'valid_from', render: (v) => dateTime(v) },
                { title: 'АЗС', dataIndex: 'station' },
                { title: 'Топливо', dataIndex: 'fuel_name' },
                numCol<R>('₽/л', 'price_per_liter', (v) => money(v, true)),
                { title: 'Основание', dataIndex: 'comment' },
                { title: 'Кто', dataIndex: 'created_by' },
                { title: '', key: 'd', render: (_, r) => <Popconfirm title="Удалить цену? Маржа будет пересчитана." onConfirm={() => remove(r.id)}><Button size="small" type="text" danger icon={<DeleteOutlined />} /></Popconfirm> },
              ]} />
          </Card>
          <Modal open={!!add} title={`${add?.receipt_id ? 'Цена слива' : 'Цена остатка'}: ${add?.fuel_name ?? ''} · ${add?.station ?? ''}`} onOk={submit} onCancel={() => setAdd(null)} okText="Сохранить" destroyOnClose>
            <Form form={form} layout="vertical">
              <Form.Item name="price" label={`Цена за литр, ₽ (${vatLabel})`} rules={[{ required: true, type: 'number', min: 0.01 }]}><InputNumber style={{ width: '100%' }} decimalSeparator="," step={0.1} /></Form.Item>
              <Form.Item name="valid_from" label={add?.receipt_id ? 'Время слива' : 'Цена остатка на'} rules={[{ required: true }]}
                extra={add?.receipt_id ? 'Средняя цена резервуара пересчитается с момента слива' : 'С этого момента средняя цена всех резервуаров этого топлива равна этой цене; дальше её пересчитывают сливы'}>
                <DatePicker showTime format="DD.MM.YYYY HH:mm" style={{ width: '100%' }} />
              </Form.Item>
              <Form.Item name="comment" label="Основание"><Input placeholder="например, ТТН №…" /></Form.Item>
              {!add?.receipt_id && <Form.Item name="all" valuePropName="checked"><Checkbox>Та же цена остатка для этого вида топлива на всех моих АЗС</Checkbox></Form.Item>}
            </Form>
          </Modal>
        </>
      )}
    </QueryState>
  );
}

function ItemCosts() {
  const me = useMe();
  const meta = useMeta();
  const qc = useQueryClient();
  const { message } = App.useApp();
  const { data, error, isLoading } = useQuery({ queryKey: ['item-costs'], queryFn: () => api<{ items: R[]; history: R[] }>('/costs/items') });
  const [station, setStation] = useState<string | null>(isOwner(me) ? null : meta.data?.stations[0]?.id ?? null);
  const [importOpen, setImportOpen] = useState(false);
  const [csv, setCsv] = useState('');
  const [result, setResult] = useState<R | null>(null);
  const [search, setSearch] = useState('');

  const setCost = async (r: R, cost: number | null) => {
    if (!cost || cost === Number(r.cost)) return;
    try {
      await api('/costs/items', { method: 'POST', body: { station_id: station, item_key: r.item_key, valid_from: dayjs().startOf('day').toISOString(), cost } });
      qc.invalidateQueries();
      message.success(`${r.name}: ${money(cost, true)}`);
    } catch (e) {
      message.error((e as Error).message);
    }
  };
  const runImport = async () => {
    try {
      const r = await api<R>('/costs/items/import', { method: 'POST', body: { station_id: station, csv, valid_from: dayjs().startOf('day').toISOString() } });
      setResult(r);
      qc.invalidateQueries();
    } catch (e) {
      message.error((e as Error).message);
    }
  };
  const readFile = async (f: File) => {
    const buf = await f.arrayBuffer();
    let text = new TextDecoder('utf-8').decode(buf);
    if (text.includes('�')) text = new TextDecoder('windows-1251').decode(buf); // Excel "CSV" in Russian Windows
    setCsv(text);
  };
  const rows = (data?.items ?? []).filter((r) => !search || `${r.name} ${r.item_code ?? ''}`.toLowerCase().includes(search.toLowerCase()));

  return (
    <QueryState error={error} loading={isLoading}>
      <Space wrap style={{ marginBottom: 8 }}>
        <span>Цены для:</span>
        <Select style={{ width: 240 }} value={station ?? 'all'} onChange={(v) => setStation(v === 'all' ? null : v)}
          options={[...(isOwner(me) ? [{ value: 'all', label: 'всех АЗС' }] : []), ...(meta.data?.stations ?? []).map((s) => ({ value: s.id, label: s.name }))]} />
        <Input.Search allowClear placeholder="Товар или штрихкод" style={{ width: 240 }} onSearch={setSearch} />
        <Button icon={<UploadOutlined />} onClick={() => { setImportOpen(true); setResult(null); setCsv(''); }}>Загрузить из Excel (CSV)</Button>
      </Space>
      <Table size="small" rowKey="item_key" dataSource={rows} pagination={{ pageSize: 50 }} scroll={{ x: 800 }}
        columns={[
          { title: 'Товар', dataIndex: 'name' },
          { title: 'Группа', dataIndex: 'group_name' },
          { title: 'Штрихкод', dataIndex: 'item_code' },
          numCol<R>('Цена продажи', 'retail_price', (v) => money(v, true)),
          {
            title: 'Себестоимость', dataIndex: 'cost', align: 'right', render: (v: number | null, r) => (
              <InputNumber size="small" min={0} style={{ width: 120 }} decimalSeparator="," defaultValue={v ?? undefined} placeholder="не задана"
                onBlur={(e) => setCost(r, Number(e.target.value.replace(',', '.')) || null)} onPressEnter={(e) => setCost(r, Number((e.target as HTMLInputElement).value.replace(',', '.')) || null)} />
            ),
          },
          numCol<R>('Наценка', 'markup', pct),
          { title: 'С', dataIndex: 'valid_from', render: (v) => (v ? dateTime(v).slice(0, 10) : '') },
          { title: '', dataIndex: 'cost_station', render: (v) => (v ? <Tag>своя для АЗС</Tag> : null) },
        ]} />
      <Modal open={importOpen} title="Загрузка себестоимости из Excel" width={640} onCancel={() => setImportOpen(false)} onOk={runImport} okText="Загрузить" okButtonProps={{ disabled: !csv.trim() }}>
        <Typography.Paragraph>
          Сохраните таблицу в Excel как <b>CSV (разделитель — точка с запятой)</b>. Колонки: <code>штрихкод или название; цена; дата (необязательно)</code>.
          Товары ищутся по штрихкоду, если его нет — по названию. Цены применяются {station ? 'к выбранной АЗС' : 'ко всем АЗС'}.
        </Typography.Paragraph>
        <input type="file" accept=".csv,.txt" onChange={(e) => e.target.files?.[0] && readFile(e.target.files[0])} />
        <Input.TextArea rows={8} style={{ marginTop: 8, fontFamily: 'monospace' }} value={csv} onChange={(e) => setCsv(e.target.value)} placeholder={'4600494694202;95\nКофе американо 0,3;45;01.10.2026'} />
        {result && (
          <Alert style={{ marginTop: 8 }} type={result.unmatched.length ? 'warning' : 'success'} showIcon
            message={`Загружено: ${result.matched}. Не найдено: ${result.unmatched.length}`}
            description={result.unmatched.slice(0, 20).map((u: R) => <div key={u.line}>строка {u.line}: {u.text} — {u.reason}</div>)} />
        )}
      </Modal>
    </QueryState>
  );
}

export default function CostsPage() {
  return (
    <>
      <Alert type="info" showIcon style={{ marginBottom: 12 }}
        message="В Топазе нет закупочных цен, поэтому их вводят здесь. Маржа во всех отчётах пересчитывается автоматически, в том числе задним числом." />
      <Tabs destroyInactiveTabPane items={[
        { key: 'fuel', label: 'Топливо', children: <><FilterBar granularity={false} compare={false} /><FuelCosts /></> },
        { key: 'items', label: 'Товары', children: <ItemCosts /> },
      ]} />
    </>
  );
}
