import { ArrowDownOutlined, ArrowUpOutlined, CheckOutlined, DeleteOutlined, PlusOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, App, Button, Flex, Input, Modal, Popconfirm, Select, Space, Switch, Table, Tabs, Tag, Tooltip, Typography } from 'antd';
import { useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useMe } from '../auth/auth';
import { api, isSuper, useMeta } from '../api/client';
import { dateTime } from '../lib/format';

interface FuelType { code: string; name: string; sort: number; is_active: boolean; confirmed: number; suggested: number; in_use: boolean }
interface FuelRow {
  station_id: string; station: string; fuel_id: number; fuel_name: string; fuel_type: string; confirmed: boolean; is_deleted: boolean;
  suggested_type: string; suggestion_source: 'learned' | 'name' | 'none'; suggested_from: string | null; needs_confirmation: boolean;
  confirmed_by: string | null; confirmed_at: string | null;
}

function useRefresh() {
  const qc = useQueryClient();
  return () => qc.invalidateQueries();
}

function source(r: FuelRow) {
  if (r.suggestion_source === 'learned') return <Tag color="blue">как на {r.suggested_from ?? 'других АЗС'}</Tag>;
  if (r.suggestion_source === 'name') return <Tag>по названию</Tag>;
  return <Tag color="orange">не распознано</Tag>;
}

/** Station fuels → reference fuel types. Unconfirmed ones use the suggested type in reports until confirmed. */
function Mapping() {
  const refresh = useRefresh();
  const { message } = App.useApp();
  const meta = useMeta();
  const [params, setParams] = useSearchParams();
  const station = params.get('station') ?? undefined;
  const [all, setAll] = useState(false);
  const [draft, setDraft] = useState<Record<string, string>>({});
  const { data, isLoading } = useQuery({
    queryKey: ['catalog-fuels', station, all],
    queryFn: () => api<{ types: { code: string; name: string; is_active: boolean }[]; fuels: FuelRow[] }>(
      `/catalog/fuels?${new URLSearchParams({ ...(station ? { station } : {}), ...(all ? {} : { unconfirmed: '1' }) })}`),
  });
  const typeName = Object.fromEntries((data?.types ?? []).map((t) => [t.code, t.name]));
  const options = (data?.types ?? []).filter((t) => t.is_active).map((t) => ({ value: t.code, label: t.name }));
  const key = (r: FuelRow) => `${r.station_id}/${r.fuel_id}`;
  const pending = (data?.fuels ?? []).filter((r) => r.needs_confirmation).length;

  const run = async (fn: () => Promise<unknown>, ok: string) => {
    try {
      await fn();
      message.success(ok);
      setDraft({});
      refresh();
    } catch (e) {
      message.error((e as Error).message);
    }
  };
  const confirm = (r: FuelRow, type: string | null) =>
    run(() => api(`/catalog/fuels/${r.station_id}/${r.fuel_id}`, { method: 'PUT', body: { fuel_type: type } }), type ? 'Сопоставление подтверждено' : 'Подтверждение снято');
  const confirmAll = () =>
    run(() => api<{ confirmed: number }>('/catalog/fuels/confirm', { method: 'POST', body: { station_id: station ?? null } }), 'Предложения приняты');

  return (
    <>
      <Typography.Paragraph type="secondary">
        Названия топлива в Топазе на разных АЗС отличаются. Здесь каждое топливо АЗС сопоставляется с видом из справочника — по нему строятся сводные отчёты и сравнение АЗС.
        Новое топливо получает предложенный вид (как на других АЗС с тем же названием или по названию) и ждёт подтверждения; до этого отчёты используют предложение.
      </Typography.Paragraph>
      <Flex wrap gap={8} align="center" style={{ marginBottom: 12 }}>
        <Select allowClear placeholder="Все АЗС" style={{ width: 240 }} value={station}
          onChange={(v) => { const p = new URLSearchParams(params); if (v) p.set('station', v); else p.delete('station'); setParams(p); }}
          options={(meta.data?.stations ?? []).map((s) => ({ value: s.id, label: s.name }))} optionFilterProp="label" showSearch />
        <Space><Switch checked={all} onChange={setAll} /> показать подтверждённые</Space>
        <Popconfirm title={`Принять предложенные виды для ${pending} топлив${station ? ' этой АЗС' : ''}?`} onConfirm={confirmAll} disabled={pending === 0} okText="Принять" cancelText="Отмена">
          <Button type="primary" icon={<CheckOutlined />} disabled={pending === 0}>Подтвердить все предложения{pending ? ` (${pending})` : ''}</Button>
        </Popconfirm>
      </Flex>
      {!isLoading && !all && pending === 0 && <Alert type="success" showIcon message="Всё топливо сопоставлено со справочником" style={{ marginBottom: 12 }} />}
      <Table<FuelRow> size="small" rowKey={key} loading={isLoading} dataSource={data?.fuels} pagination={false} scroll={{ x: 860 }}
        locale={{ emptyText: all ? 'Топлива нет' : 'Нет топлива, ожидающего подтверждения' }}
        columns={[
          { title: 'АЗС', dataIndex: 'station', width: 200 },
          { title: 'Топливо в Топазе', dataIndex: 'fuel_name', render: (v, r) => <>{v} {r.is_deleted && <Tag>удалено</Tag>}</> },
          {
            title: 'Предложение', key: 'sug', render: (_, r) => (r.confirmed ? null : <>{typeName[r.suggested_type]} {source(r)}</>),
          },
          {
            title: 'Вид в справочнике', key: 'type', width: 220, render: (_, r) => (
              <Select style={{ width: 200 }} value={draft[key(r)] ?? r.fuel_type} options={options}
                onChange={(t) => (r.confirmed ? confirm(r, t) : setDraft({ ...draft, [key(r)]: t }))} />
            ),
          },
          {
            title: '', key: 'a', width: 230, render: (_, r) => (r.confirmed
              ? <Space size={4}><Tooltip title={`${r.confirmed_by ?? ''} ${r.confirmed_at ? dateTime(r.confirmed_at) : ''}`}><Tag color="green">подтверждено</Tag></Tooltip>
                <Button size="small" type="link" onClick={() => confirm(r, null)}>снять</Button></Space>
              : <Button size="small" icon={<CheckOutlined />} onClick={() => confirm(r, draft[key(r)] ?? r.suggested_type)}>Подтвердить</Button>),
          },
        ]} />
    </>
  );
}

/** The reference list itself: add, rename, reorder, hide, delete unused. */
function Reference() {
  const refresh = useRefresh();
  const edit = isSuper(useMe());
  const { message } = App.useApp();
  const { data, isLoading } = useQuery({ queryKey: ['fuel-types'], queryFn: () => api<FuelType[]>('/catalog/fuel-types') });
  const [adding, setAdding] = useState(false);
  const [name, setName] = useState('');
  const rows = data ?? [];

  const call = async (fn: () => Promise<unknown>) => {
    try {
      await fn();
      refresh();
      return true;
    } catch (e) {
      message.error((e as Error).message);
      return false;
    }
  };
  const update = (code: string, body: Record<string, unknown>) => call(() => api(`/catalog/fuel-types/${code}`, { method: 'PUT', body }));
  const move = async (i: number, dir: -1 | 1) => {
    const order = [...rows];
    [order[i], order[i + dir]] = [order[i + dir], order[i]];
    for (const [j, r] of order.entries()) if (r.sort !== j + 1 && !(await update(r.code, { sort: j + 1 }))) return;
  };

  return (
    <>
      <Typography.Paragraph type="secondary">
        Эталонные виды топлива. Скрытый вид не предлагается для новых сопоставлений, но уже сопоставленное топливо остаётся в нём.
        Удалить можно только вид, который нигде не используется. «Прочее» — для нераспознанного топлива, его нельзя скрыть или удалить.
        {!edit && ' Справочник общий для всех организаций, его ведёт администратор платформы.'}
      </Typography.Paragraph>
      {edit && <Button type="primary" icon={<PlusOutlined />} onClick={() => { setName(''); setAdding(true); }} style={{ marginBottom: 12 }}>Добавить вид</Button>}
      <Table<FuelType> size="small" rowKey="code" loading={isLoading} dataSource={rows} pagination={false} scroll={{ x: 700 }}
        columns={edit ? [
          {
            title: 'Порядок', key: 'm', width: 90, render: (_, _r, i) => (
              <Space size={0}>
                <Button size="small" type="text" icon={<ArrowUpOutlined />} disabled={i === 0} onClick={() => move(i, -1)} aria-label="выше" />
                <Button size="small" type="text" icon={<ArrowDownOutlined />} disabled={i === rows.length - 1} onClick={() => move(i, 1)} aria-label="ниже" />
              </Space>
            ),
          },
          {
            title: 'Название', dataIndex: 'name', render: (v: string, r) => (
              <Typography.Text editable={{ onChange: (n) => { if (n.trim() && n.trim() !== v) update(r.code, { name: n.trim() }); }, triggerType: ['icon', 'text'] }}>{v}</Typography.Text>
            ),
          },
          { title: 'Сопоставлено', dataIndex: 'confirmed', width: 130, render: (v: number, r) => <>{v}{r.suggested > 0 && <Typography.Text type="secondary"> + {r.suggested} предл.</Typography.Text>}</> },
          {
            title: 'Активен', dataIndex: 'is_active', width: 90,
            render: (v: boolean, r) => <Switch size="small" checked={v} disabled={r.code === 'OTHER'} onChange={(c) => update(r.code, { is_active: c })} />,
          },
          {
            title: '', key: 'd', width: 60, render: (_, r) => (r.in_use ? null : (
              <Popconfirm title={`Удалить «${r.name}»?`} onConfirm={() => call(() => api(`/catalog/fuel-types/${r.code}`, { method: 'DELETE' }))} okText="Удалить" cancelText="Отмена">
                <Button size="small" danger type="text" icon={<DeleteOutlined />} aria-label="удалить" />
              </Popconfirm>
            )),
          },
        ] : [
          { title: 'Название', dataIndex: 'name' },
          { title: 'Сопоставлено', dataIndex: 'confirmed', width: 130, render: (v: number, r) => <>{v}{r.suggested > 0 && <Typography.Text type="secondary"> + {r.suggested} предл.</Typography.Text>}</> },
          { title: 'Активен', dataIndex: 'is_active', width: 90, render: (v: boolean) => (v ? 'да' : 'скрыт') },
        ]} />
      <Modal open={adding} title="Новый вид топлива" okText="Добавить" cancelText="Отмена" onCancel={() => setAdding(false)} okButtonProps={{ disabled: !name.trim() }}
        onOk={async () => { if (await call(() => api('/catalog/fuel-types', { method: 'POST', body: { name: name.trim() } }))) setAdding(false); }}>
        <Input autoFocus placeholder="например, АИ-95 премиум или ДТ зимнее" value={name} maxLength={60} onChange={(e) => setName(e.target.value)} />
      </Modal>
    </>
  );
}

export function FuelCatalog() {
  const meta = useMeta();
  const [params, setParams] = useSearchParams();
  const view = params.get('view') ?? 'mapping';
  const unmapped = meta.data?.unmapped_fuels ?? 0;
  return (
    <Tabs size="small" activeKey={view} onChange={(k) => { const p = new URLSearchParams(params); p.set('view', k); setParams(p); }} items={[
      { key: 'mapping', label: <>Сопоставление {unmapped > 0 && <Tag color="orange" style={{ marginInlineStart: 4 }}>{unmapped}</Tag>}</>, children: <Mapping /> },
      { key: 'reference', label: 'Справочник', children: <Reference /> },
    ]} />
  );
}
