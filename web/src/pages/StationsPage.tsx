import { EditOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { App, Badge, Button, Form, Input, Modal, Select, Table, Tabs, Tag, Tooltip } from 'antd';
import StationCompare from './StationCompare';
import { useState } from 'react';
import { api } from '../api/client';
import { useMe } from '../auth/auth';
import { PageTitle, QueryState } from '../components/ui';
import { dateTime, int } from '../lib/format';

interface StationRow {
  id: string; name: string; topaz_name: string; display_name: string | null; time_zone: string; time_zone_manual: boolean; region: string | null;
  group_name: string | null; address: string | null; status: string; agent_version: string | null; machine_name: string | null;
  last_seen_at: string | null; silent_seconds: number | null; agent_queue_pending: number | null; agent_queue_dead: number | null;
  topaz_db_ok: boolean | null; last_collect_error: string | null; server_backlog: number; server_failed: number; topaz_db_version: string | null;
  last_session_id: number | null; last_session_open: boolean | null; last_sale_at: string | null;
}

const ZONES = ['Europe/Kaliningrad', 'Europe/Moscow', 'Europe/Samara', 'Asia/Yekaterinburg', 'Asia/Omsk', 'Asia/Novosibirsk', 'Asia/Krasnoyarsk',
  'Asia/Irkutsk', 'Asia/Yakutsk', 'Asia/Vladivostok', 'Asia/Magadan', 'Asia/Kamchatka', 'Europe/Minsk', 'Asia/Almaty', 'Asia/Tashkent'];

function status(r: StationRow) {
  if (r.status !== 'active') return <Badge status="default" text={r.status === 'revoked' ? 'заблокирована' : 'ждёт переустановки'} />;
  if (r.silent_seconds === null) return <Badge status="default" text="не выходила на связь" />;
  if (r.silent_seconds > 900) return <Badge status="error" text={`нет связи ${Math.round(r.silent_seconds / 60)} мин`} />;
  if (r.topaz_db_ok === false) return <Tooltip title={r.last_collect_error}><Badge status="error" text="нет доступа к базе Топаза" /></Tooltip>;
  if ((r.agent_queue_dead ?? 0) > 0 || r.server_failed > 0) return <Badge status="warning" text="есть отклонённые пакеты" />;
  if ((r.agent_queue_pending ?? 0) > 20) return <Badge status="processing" text={`догружает: ${r.agent_queue_pending}`} />;
  return <Badge status="success" text="на связи" />;
}

export default function StationsPage() {
  const me = useMe();
  const qc = useQueryClient();
  const { message } = App.useApp();
  const { data, error, isLoading } = useQuery({ queryKey: ['stations'], queryFn: () => api<StationRow[]>('/stations'), refetchInterval: 30_000 });
  const [edit, setEdit] = useState<StationRow | null>(null);
  const [form] = Form.useForm();

  const save = async () => {
    const v = await form.validateFields();
    try {
      await api(`/stations/${edit!.id}`, { method: 'PATCH', body: { ...v, time_zone: v.time_zone === edit!.time_zone ? null : v.time_zone } });
      message.success('Сохранено');
      setEdit(null);
      qc.invalidateQueries();
    } catch (e) {
      message.error((e as Error).message);
    }
  };

  return (
    <>
      <PageTitle title="АЗС" />
      <Tabs destroyInactiveTabPane items={[
        { key: 'compare', label: 'Сравнение', children: <StationCompare /> },
        { key: 'status', label: 'Состояние и настройки', children: (
      <QueryState error={error} loading={isLoading}>
        <Table<StationRow>
          size="small"
          rowKey="id"
          dataSource={data}
          pagination={false}
          scroll={{ x: 1100 }}
          columns={[
            { title: 'АЗС', dataIndex: 'name', key: 'name', fixed: 'left', render: (v, r) => <>{v}{r.display_name && <div style={{ opacity: 0.6, fontSize: 12 }}>в Топазе: {r.topaz_name}</div>}</> },
            { title: 'Состояние', key: 'st', render: (_, r) => status(r) },
            { title: 'Последняя связь', dataIndex: 'last_seen_at', key: 'seen', render: (v) => dateTime(v) },
            { title: 'Последняя продажа', dataIndex: 'last_sale_at', key: 'sale', render: (v, r) => dateTime(v, r.time_zone) },
            { title: 'Смена', key: 'sess', render: (_, r) => (r.last_session_id ? <>№{r.last_session_id} {r.last_session_open ? <Tag color="green">открыта</Tag> : <Tag>закрыта</Tag>}</> : '—') },
            { title: 'Очередь агента', dataIndex: 'agent_queue_pending', key: 'q', align: 'right', render: (v) => int(v) },
            { title: 'Регион / группа', key: 'rg', render: (_, r) => [r.region, r.group_name].filter(Boolean).join(' / ') || '—' },
            { title: 'Часовой пояс', dataIndex: 'time_zone', key: 'tz' },
            { title: 'Агент', key: 'ag', render: (_, r) => <>{r.agent_version ?? '—'}<div style={{ opacity: 0.6, fontSize: 12 }}>{r.machine_name} · Firebird {r.topaz_db_version ?? '?'}</div></> },
            ...(me.role === 'owner' ? [{ title: '', key: 'e', width: 48, render: (_: unknown, r: StationRow) => <Button type="text" icon={<EditOutlined />} onClick={() => { setEdit(r); form.setFieldsValue(r); }} /> }] : []),
          ]}
        />
      </QueryState>
        ) },
      ]} />
      <Modal open={!!edit} title={`АЗС: ${edit?.topaz_name}`} onOk={save} onCancel={() => setEdit(null)} okText="Сохранить" destroyOnClose>
        <Form form={form} layout="vertical">
          <Form.Item name="display_name" label="Название в интерфейсе" extra="Пусто — как в Топазе / при установке агента"><Input maxLength={100} /></Form.Item>
          <Form.Item name="time_zone" label="Часовой пояс" extra="После ручной установки агент его больше не меняет">
            <Select showSearch options={ZONES.map((z) => ({ value: z, label: z }))} />
          </Form.Item>
          <Form.Item name="region" label="Регион"><Input maxLength={100} /></Form.Item>
          <Form.Item name="group_name" label="Группа"><Input maxLength={100} /></Form.Item>
          <Form.Item name="address" label="Адрес"><Input maxLength={300} /></Form.Item>
        </Form>
      </Modal>
    </>
  );
}
