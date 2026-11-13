import { EditOutlined, MoreOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { App, Badge, Button, Dropdown, Form, Input, Modal, Progress, Select, Table, Tag, Tooltip } from 'antd';
import { useState } from 'react';
import { api, isOwner, isSuper } from '../api/client';
import { useMe } from '../auth/auth';
import { QueryState } from '../components/ui';
import { dateTime, dec, int } from '../lib/format';

interface StationRow {
  id: string; name: string; topaz_name: string; display_name: string | null; time_zone: string; time_zone_manual: boolean; region: string | null;
  group_name: string | null; address: string | null; status: string; agent_version: string | null; machine_name: string | null;
  last_seen_at: string | null; silent_seconds: number | null; agent_queue_pending: number | null; agent_queue_dead: number | null;
  topaz_db_ok: boolean | null; last_collect_error: string | null; server_backlog: number; server_failed: number; topaz_db_version: string | null;
  last_session_id: number | null; last_session_open: boolean | null; last_sale_at: string | null;
  host_checked_at: string | null; clock_skew_seconds: number | null; db_disk_free: number | null; db_disk_total: number | null; db_size: number | null;
  sys_disk_free: number | null; sys_disk_total: number | null; db_growth_per_week: number | null; days_until_full: number | null;
  uptime_seconds: number | null; os: string | null; windows_time_zone: string | null; disk_low: boolean | null; clock_off: boolean | null;
}

const GB = 1024 ** 3;
const gb = (b: number) => `${dec(b / GB, b < 10 * GB ? 1 : 0)} ГБ`;
const minFree = (r: StationRow) => Math.min(...[r.db_disk_free, r.sys_disk_free].filter((x): x is number => x != null));
// thresholds are the "Контроль" rules (disk_low, clock_skew); the server applies them
const lowDisk = (r: StationRow) => r.disk_low === true;
const clockOff = (r: StationRow) => r.clock_off === true;

function clockText(s: number) {
  const m = Math.round(Math.abs(s) / 60);
  return m === 0 ? 'точно' : `${s > 0 ? 'спешат' : 'отстают'} на ${m} мин`;
}

/** Station PC health (agent 1.2.1+): disk with the Topaz DB, DB size and growth, clock. */
function hostCell(r: StationRow) {
  if (!r.host_checked_at) return <Tooltip title="Агент до версии 1.2.1 не сообщает о ПК — обновите его."><span style={{ opacity: 0.5 }}>—</span></Tooltip>;
  const free = r.db_disk_free ?? r.sys_disk_free;
  const total = r.db_disk_free != null ? r.db_disk_total : r.sys_disk_total;
  const details = [
    r.db_disk_free != null && `Диск с базой: свободно ${gb(r.db_disk_free)} из ${gb(r.db_disk_total ?? 0)}`,
    r.sys_disk_free != null && `Системный диск: свободно ${gb(r.sys_disk_free)} из ${gb(r.sys_disk_total ?? 0)}`,
    r.days_until_full != null && `Место закончится примерно через ${int(r.days_until_full)} дн.`,
    r.windows_time_zone && `Часовой пояс Windows: ${r.windows_time_zone}`,
    r.uptime_seconds != null && `Windows работает ${r.uptime_seconds >= 86400 ? `${Math.floor(r.uptime_seconds / 86400)} дн.` : `${Math.round(r.uptime_seconds / 3600)} ч`}`,
    r.os,
  ].filter(Boolean);
  return (
    <Tooltip title={<>{details.map((d) => <div key={String(d)}>{d}</div>)}</>}>
      <div style={{ minWidth: 170, fontSize: 13 }}>
        {free != null && total ? (
          <>
            <Progress percent={Math.round(100 * (1 - free / total))} size="small" showInfo={false} status={lowDisk(r) ? 'exception' : 'normal'} style={{ margin: 0 }} />
            <span style={{ color: lowDisk(r) ? '#cf1322' : undefined }}>свободно {gb(free)} из {gb(total)}</span>
          </>
        ) : null}
        {r.db_size != null && <div style={{ opacity: 0.7 }}>база {gb(r.db_size)}{r.db_growth_per_week != null && r.db_growth_per_week > 0 ? `, +${int(r.db_growth_per_week / 1024 ** 2)} МБ/нед` : ''}</div>}
        {r.clock_skew_seconds != null && <div style={{ color: clockOff(r) ? '#cf1322' : undefined, opacity: clockOff(r) ? 1 : 0.7 }}>часы {clockText(r.clock_skew_seconds)}</div>}
      </div>
    </Tooltip>
  );
}

const ZONES = ['Europe/Kaliningrad', 'Europe/Moscow', 'Europe/Samara', 'Asia/Yekaterinburg', 'Asia/Omsk', 'Asia/Novosibirsk', 'Asia/Krasnoyarsk',
  'Asia/Irkutsk', 'Asia/Yakutsk', 'Asia/Vladivostok', 'Asia/Magadan', 'Asia/Kamchatka', 'Europe/Minsk', 'Asia/Almaty', 'Asia/Tashkent'];

function status(r: StationRow) {
  if (r.status !== 'active') return r.status === 'revoked'
    ? <Badge status="error" text="заблокирована" />
    : <Tooltip title="Доступ перевыпущен: агент пришлёт новую заявку, её нужно одобрить в «Платформа → Подключение АЗС»."><Badge status="warning" text="ждёт новой заявки" /></Tooltip>;
  if (r.silent_seconds === null) return <Badge status="default" text="не выходила на связь" />;
  if (r.silent_seconds > 900) return <Badge status="error" text={`нет связи ${Math.round(r.silent_seconds / 60)} мин`} />;
  if (r.topaz_db_ok === false) return <Tooltip title={r.last_collect_error}><Badge status="error" text="нет доступа к базе Топаза" /></Tooltip>;
  if ((r.agent_queue_dead ?? 0) > 0 || r.server_failed > 0) return <Badge status="warning" text="есть отклонённые пакеты" />;
  if (lowDisk(r)) return <Badge status="error" text={`мало места: ${gb(minFree(r))}`} />;
  if (clockOff(r)) return <Badge status="warning" text={`часы ${clockText(r.clock_skew_seconds!)}`} />;
  if ((r.agent_queue_pending ?? 0) > 20) return <Badge status="processing" text={`догружает: ${r.agent_queue_pending}`} />;
  return <Badge status="success" text="на связи" />;
}

export default function StationsPage() {
  const me = useMe();
  const qc = useQueryClient();
  const { message } = App.useApp();
  const { data, error, isLoading } = useQuery({ queryKey: ['stations'], queryFn: () => api<StationRow[]>('/stations'), refetchInterval: 30_000 });
  const [edit, setEdit] = useState<StationRow | null>(null);
  const { modal } = App.useApp();

  /** The station changes owner: its whole history moves with it (superadmin). */
  const move = (r: StationRow) => {
    let target: string | undefined;
    modal.confirm({
      title: `Передать «${r.name}» другой организации`,
      content: (
        <>
          <p>Вся история АЗС перейдёт к новой организации; её управляющие и наблюдатели в текущей организации потеряют доступ.</p>
          <Select style={{ width: '100%' }} placeholder="Организация" onChange={(v) => { target = v; }}
            options={(me.organizations ?? []).filter((o) => o.id !== me.organization_id).map((o) => ({ value: o.id, label: o.name }))} />
        </>
      ),
      okText: 'Передать', cancelText: 'Отмена',
      onOk: async () => {
        if (!target) { message.error('Выберите организацию'); throw new Error('no target'); }
        await api(`/stations/${r.id}/organization`, { method: 'POST', body: { organization_id: target } });
        message.success('АЗС передана');
        qc.invalidateQueries();
      },
    });
  };

  const access = (r: StationRow, action: 'block' | 'unblock' | 'reissue') => {
    const text = {
      block: { title: `Заблокировать «${r.name}»?`, content: 'Агент сразу потеряет доступ к серверу (данные будут копиться на АЗС). История АЗС сохранится, разблокировать можно в любой момент.' },
      unblock: { title: `Разблокировать «${r.name}»?`, content: 'Агент снова сможет отправлять данные со своим прежним доступом.' },
      reissue: { title: `Перевыпустить доступ «${r.name}»?`, content: 'Текущий доступ агента перестанет действовать. Агент пришлёт новую заявку с новым кодом — её нужно будет одобрить как «существующая АЗС». Используйте при переносе на другой ПК или подозрении на утечку.' },
    }[action];
    modal.confirm({
      ...text, okText: 'Да', cancelText: 'Отмена', okButtonProps: { danger: action !== 'unblock' },
      onOk: async () => {
        try {
          await api(`/stations/${r.id}/access`, { method: 'POST', body: { action } });
          message.success('Готово');
          qc.invalidateQueries();
        } catch (e) {
          message.error((e as Error).message);
        }
      },
    });
  };
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
      <QueryState error={error} loading={isLoading}>
        <Table<StationRow>
          size="small"
          rowKey="id"
          dataSource={data}
          pagination={false}
          scroll={{ x: 1450 }}
          columns={[
            { title: 'АЗС', dataIndex: 'name', key: 'name', fixed: 'left', render: (v, r) => <>{v}{r.display_name && <div style={{ opacity: 0.6, fontSize: 12 }}>в Топазе: {r.topaz_name}</div>}</> },
            { title: 'Состояние', key: 'st', render: (_, r) => status(r) },
            { title: 'ПК АЗС', key: 'pc', width: 230, render: (_, r) => hostCell(r) },
            { title: 'Последняя связь', dataIndex: 'last_seen_at', key: 'seen', render: (v) => dateTime(v) },
            { title: 'Последняя продажа', dataIndex: 'last_sale_at', key: 'sale', render: (v, r) => dateTime(v, r.time_zone) },
            { title: 'Смена', key: 'sess', render: (_, r) => (r.last_session_id ? <>№{r.last_session_id} {r.last_session_open ? <Tag color="green">открыта</Tag> : <Tag>закрыта</Tag>}</> : '—') },
            { title: 'Очередь агента', dataIndex: 'agent_queue_pending', key: 'q', align: 'right', render: (v) => int(v) },
            { title: 'Регион / группа', key: 'rg', render: (_, r) => [r.region, r.group_name].filter(Boolean).join(' / ') || '—' },
            { title: 'Часовой пояс', dataIndex: 'time_zone', key: 'tz' },
            { title: 'Агент', key: 'ag', render: (_, r) => <>{r.agent_version ?? '—'}<div style={{ opacity: 0.6, fontSize: 12 }}>{r.machine_name} · Firebird {r.topaz_db_version ?? '?'}</div></> },
            ...(isOwner(me) ? [{
              title: '', key: 'e', width: 88, render: (_: unknown, r: StationRow) => (
                <>
                  <Button type="text" icon={<EditOutlined />} aria-label="настройки АЗС" onClick={() => { setEdit(r); form.setFieldsValue(r); }} />
                  {isSuper(me) && (
                    <Dropdown trigger={['click']} menu={{
                      items: [
                        r.status === 'revoked'
                          ? { key: 'unblock', label: 'Разблокировать', onClick: () => access(r, 'unblock') }
                          : { key: 'block', label: 'Заблокировать', danger: true, onClick: () => access(r, 'block') },
                        { key: 'reissue', label: 'Перевыпустить доступ', onClick: () => access(r, 'reissue') },
                        { key: 'move', label: 'Передать другой организации…', onClick: () => move(r) },
                      ],
                    }}><Button type="text" icon={<MoreOutlined />} aria-label="доступ АЗС" /></Dropdown>
                  )}
                </>
              ),
            }] : []),
          ]}
        />
      </QueryState>
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
