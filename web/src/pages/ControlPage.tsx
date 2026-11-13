import { DeleteOutlined, ReloadOutlined, SendOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, App, Button, Card, Form, Input, InputNumber, Modal, Popconfirm, Segmented, Select, Space, Switch, Table, Tag, Typography } from 'antd';
import { useState } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { api, isOwner, isSuper, useFilter, useFiltered } from '../api/client';
import { filterQuery } from '../lib/filter';
import { useMe } from '../auth/auth';
import { FilterBar } from '../components/FilterBar';
import { QueryState } from '../components/ui';
import { Section } from '../components/Section';
import { section } from '../nav';
import { dateTime } from '../lib/format';
import { SEVERITY, severityOptions } from '../lib/severity';
import { openTelegram, TelegramPersonal } from '../components/TelegramPersonal';

type R = Record<string, any>;
export { SEVERITY };

export function Alerts() {
  const qc = useQueryClient();
  const location = useLocation();
  const [status, setStatus] = useState('open');
  const [severity, setSeverity] = useState<string | undefined>();
  const [rule, setRule] = useState<string | undefined>();
  const { data, error, isLoading } = useFiltered<{ alerts: R[]; by_rule: R[] }>('/alerts', { status: status === 'all' ? undefined : status, severity, rule });
  const set = async (id: number, s: string) => {
    await api(`/alerts/${id}/status`, { method: 'POST', body: { status: s } });
    qc.invalidateQueries({ queryKey: ['/alerts'] });
    qc.invalidateQueries({ queryKey: ['alerts-summary'] });
  };
  const [filter] = useFilter();
  const { message } = App.useApp();
  const [bulk, setBulk] = useState<string | null>(null);
  // «Просмотреть все» / «Закрыть все»: every alert of the current filter (АЗС, период, важность, правило), not just this page.
  const setAll = async (s: 'seen' | 'closed') => {
    setBulk(s);
    try {
      const r = await api<{ changed: number }>(`/alerts/status?${filterQuery(filter, { severity, rule })}`, { method: 'POST', body: { status: s } });
      message.success(`${s === 'seen' ? 'Просмотрено' : 'Закрыто'} событий: ${r.changed}`);
      qc.invalidateQueries({ queryKey: ['/alerts'] });
      qc.invalidateQueries({ queryKey: ['alerts-summary'] });
    } finally {
      setBulk(null);
    }
  };
  const newCount = (data?.alerts ?? []).filter((r) => r.status === 'new').length;
  const openCount = (data?.alerts ?? []).filter((r) => r.status !== 'closed').length;
  return (
    <>
      <FilterBar granularity={false} compare={false} />
      <Space wrap style={{ marginBottom: 12 }}>
        <Segmented value={status} onChange={(v) => setStatus(String(v))} options={[{ value: 'open', label: 'Открытые' }, { value: 'new', label: 'Новые' }, { value: 'closed', label: 'Закрытые' }, { value: 'all', label: 'Все' }]} />
        <Select allowClear placeholder="Важность" style={{ width: 150 }} value={severity} onChange={setSeverity} options={Object.entries(SEVERITY).map(([k, v]) => ({ value: k, label: v.label }))} />
        <Select allowClear placeholder="Правило" style={{ width: 300 }} value={rule} onChange={setRule} options={(data?.by_rule ?? []).map((r) => ({ value: r.rule_code, label: `${r.title} (${r.total})` }))} />
        <Button disabled={newCount === 0} loading={bulk === 'seen'} onClick={() => setAll('seen')}>Просмотреть все</Button>
        <Popconfirm
          title="Закрыть все события?"
          description="Будут закрыты все незакрытые события за выбранный период по выбранным АЗС, важности и правилу."
          okText="Закрыть все" cancelText="Отмена" disabled={openCount === 0}
          onConfirm={() => setAll('closed')}>
          <Button disabled={openCount === 0} loading={bulk === 'closed'}>Закрыть все</Button>
        </Popconfirm>
      </Space>
      <QueryState error={error} loading={isLoading}>
        <Table size="small" rowKey="id" dataSource={data?.alerts} pagination={{ pageSize: 50 }} scroll={{ x: 1000 }} locale={{ emptyText: 'Событий нет' }}
          columns={[
            { title: '', dataIndex: 'severity', width: 110, render: (v: string) => <Tag color={SEVERITY[v]?.color}>{SEVERITY[v]?.label}</Tag> },
            { title: 'Когда', key: 'at', width: 140, render: (_, r) => dateTime(r.occurred_at ?? r.detected_at, r.time_zone) },
            { title: 'АЗС', dataIndex: 'station', width: 180 },
            {
              title: 'Событие', key: 't', render: (_, r) => (
                <>
                  <b style={{ fontWeight: r.status === 'new' ? 600 : 400 }}>{r.title}</b>
                  {r.details && <div style={{ opacity: 0.75, fontSize: 13 }}>{r.details}</div>}
                  {r.session_id != null && <Link to={`/shifts/${r.station_id}/${r.session_id}${location.search}`}>карточка смены</Link>}
                  {r.rule_code === 'fuel_unmapped' && <Link to={`/settings/fuels?station=${r.station_id}`}>сопоставить топливо</Link>}
                  {r.rule_code === 'station_registration' && <Link to="/platform/connect">открыть заявку</Link>}
                </>
              ),
            },
            {
              title: 'Статус', key: 's', width: 220, render: (_, r) => (
                <Space size={4}>
                  {r.status === 'closed' ? <Tag>закрыто{r.status_by ? ` (${r.status_by})` : ''}</Tag> : r.status === 'seen' ? <Tag color="default">просмотрено</Tag> : <Tag color="gold">новое</Tag>}
                  {r.status === 'new' && <Button size="small" onClick={() => set(r.id, 'seen')}>Просмотрено</Button>}
                  {r.status !== 'closed' && <Button size="small" onClick={() => set(r.id, 'closed')}>Закрыть</Button>}
                  {r.status === 'closed' && <Button size="small" type="link" onClick={() => set(r.id, 'new')}>Открыть</Button>}
                </Space>
              ),
            },
          ]} />
      </QueryState>
    </>
  );
}

export function Rules() {
  const qc = useQueryClient();
  const { message } = App.useApp();
  const { data, isLoading } = useQuery({ queryKey: ['alert-rules'], queryFn: () => api<R[]>('/alert-rules') });
  const save = async (r: R, patch: R) => {
    try {
      await api(`/alert-rules/${r.code}`, { method: 'PUT', body: { enabled: r.enabled, threshold: r.threshold, severity: r.severity, ...patch } });
      qc.invalidateQueries({ queryKey: ['alert-rules'] });
    } catch (e) {
      message.error((e as Error).message);
    }
  };
  const reset = async (r: R) => {
    try {
      await api(`/alert-rules/${r.code}`, { method: 'DELETE' });
      qc.invalidateQueries({ queryKey: ['alert-rules'] });
    } catch (e) {
      message.error((e as Error).message);
    }
  };
  const run = async () => {
    const r = await api<{ found: number }>('/alerts/run', { method: 'POST' });
    message.success(`Проверка выполнена, новых событий: ${r.found}`);
    qc.invalidateQueries();
  };
  return (
    <>
      <Typography.Paragraph type="secondary">
        Правила проверяются каждые 5 минут по данным за последние 30 дней. Одно событие на объект (смену, рукав, приём) не повторяется.
        Изменённые значения действуют только для АЗС вашей организации; «Сбросить» возвращает общие значения по умолчанию.
      </Typography.Paragraph>
      <Button icon={<ReloadOutlined />} onClick={run} style={{ marginBottom: 12 }}>Проверить сейчас</Button>
      <Table size="small" rowKey="code" loading={isLoading} dataSource={data} pagination={false} scroll={{ x: 900 }}
        columns={[
          { title: 'Вкл.', dataIndex: 'enabled', width: 60, render: (v: boolean, r) => <Switch size="small" checked={v} onChange={(c) => save(r, { enabled: c })} /> },
          {
            title: 'Правило', dataIndex: 'title', render: (v, r) => (
              <>
                <b>{v}</b> {r.platform && <Tag color="purple">платформа</Tag>}
                {r.overridden && <Tag color="blue">своё значение</Tag>}
                <div style={{ fontSize: 13, opacity: 0.75 }}>{r.description}</div>
              </>
            ),
          },
          {
            title: 'Порог', dataIndex: 'threshold', width: 150, render: (v: number | null, r) => (v == null ? '—' : (
              <InputNumber key={`${r.code}:${v}`} size="small" defaultValue={Number(v)} min={0} addonAfter={r.unit} style={{ width: 130 }} onBlur={(e) => { const n = Number(e.target.value.replace(',', '.')); if (n !== Number(v)) save(r, { threshold: n }); }} />
            )),
          },
          {
            title: 'Важность', dataIndex: 'severity', width: 150, render: (v: string, r) => (
              <Select size="small" value={v} style={{ width: 130 }} onChange={(s) => save(r, { severity: s })} options={Object.entries(SEVERITY).map(([k, x]) => ({ value: k, label: x.label }))} />
            ),
          },
          {
            title: '', key: 'reset', width: 110, render: (_, r) => (r.overridden && !r.platform ? (
              <Button size="small" type="link" onClick={() => reset(r)}
                title={`По умолчанию: ${r.default_enabled ? 'вкл.' : 'выкл.'}${r.default_threshold == null ? '' : `, ${Number(r.default_threshold)} ${r.unit ?? ''}`}, ${SEVERITY[r.default_severity]?.label ?? r.default_severity}`}>Сбросить</Button>
            ) : null),
          },
        ]} />
    </>
  );
}

function Notifications() {
  const me = useMe();
  const qc = useQueryClient();
  const { message } = App.useApp();
  const [groupLink, setGroupLink] = useState<string | null>(null);
  const [groupOpen, setGroupOpen] = useState(false);
  const { data, isLoading } = useQuery({
    queryKey: ['channels'],
    queryFn: () => api<{ token_configured: boolean; channels: R[] }>('/notifications'),
    refetchInterval: groupLink ? 3000 : false, // the group appears once the bot is added there
  });
  const [open, setOpen] = useState(false);
  const [form] = Form.useForm();
  const [groupForm] = Form.useForm();
  const add = async () => {
    const v = await form.validateFields();
    try {
      await api('/notifications', { method: 'POST', body: v });
      setOpen(false);
      qc.invalidateQueries({ queryKey: ['channels'] });
    } catch (e) {
      message.error((e as Error).message);
    }
  };
  const linkGroup = async () => {
    const v = await groupForm.validateFields();
    try {
      const r = await api<{ url: string }>('/notifications/link-group', { method: 'POST', body: v });
      setGroupLink(r.url);
      openTelegram(r.url);
    } catch (e) {
      message.error((e as Error).message);
    }
  };
  const closeGroup = () => { setGroupOpen(false); setGroupLink(null); };
  const test = async (id: number) => {
    try {
      await api(`/notifications/${id}/test`, { method: 'POST' });
      message.success('Тестовое сообщение отправлено');
    } catch (e) {
      message.error((e as Error).message);
    }
  };
  const severityAndDigest = (
    <>
      <Form.Item name="min_severity" label="Присылать события с важностью от"><Select options={severityOptions} /></Form.Item>
      <Form.Item name="daily_digest" label="Утренняя сводка" valuePropName="checked"><Switch /></Form.Item>
      {isSuper(me) && (
        <Form.Item name="platform" label="Чат платформы" valuePropName="checked"
          extra="Заявки на подключение АЗС и ошибки обновления агентов. Иначе чат получает события АЗС организации, выбранной в шапке.">
          <Switch />
        </Form.Item>
      )}
    </>
  );
  return (
    <>
      {data && !data.token_configured && (
        <Alert type="warning" showIcon style={{ marginBottom: 12 }} message="Токен Telegram-бота не задан"
          description={<>Создайте бота у <code>@BotFather</code>, впишите токен в <code>TOPAZ_TELEGRAM_BOT_TOKEN</code> в файле <code>.env</code> сервера и перезапустите сервис.</>} />
      )}
      <Typography.Paragraph type="secondary">
        Сразу приходят события с важностью не ниже выбранной (только за последние сутки); утром в 9:00 по Москве — сводка за сутки.
        Каждый сотрудник может получать события по своим АЗС и лично — блок «Мой Telegram» выше.
      </Typography.Paragraph>
      <Space style={{ marginBottom: 12 }} wrap>
        <Button type="primary" icon={<SendOutlined />} disabled={!data?.token_configured}
          onClick={() => { setGroupOpen(true); groupForm.setFieldsValue({ min_severity: 'warning', daily_digest: true }); }}>Добавить бота в группу</Button>
        <Button onClick={() => { setOpen(true); form.setFieldsValue({ min_severity: 'warning', daily_digest: true }); }}>Добавить по номеру чата</Button>
      </Space>
      <Table size="small" rowKey="id" loading={isLoading} dataSource={data?.channels} pagination={false}
        columns={[
          {
            title: 'Чат', dataIndex: 'title', render: (v, r) => (
              <>
                {v ?? '—'} <Typography.Text type="secondary">{r.target}</Typography.Text>
                {r.platform && <Tag color="purple">платформа</Tag>}
                {r.user_id && <Tag color="cyan">личный</Tag>}
                {!r.enabled && <Tag>выключен</Tag>}
              </>
            ),
          },
          { title: 'Важность от', dataIndex: 'min_severity', render: (v: string) => <Tag color={SEVERITY[v]?.color}>{SEVERITY[v]?.label}</Tag> },
          { title: 'Сводка в 9:00', dataIndex: 'daily_digest', render: (v: boolean) => (v ? 'да' : 'нет') },
          {
            title: '', key: 'a', render: (_, r) => (
              <Space>
                <Button size="small" icon={<SendOutlined />} onClick={() => test(r.id)} disabled={!data?.token_configured}>Тест</Button>
                <Button size="small" danger type="text" icon={<DeleteOutlined />} onClick={async () => { await api(`/notifications/${r.id}`, { method: 'DELETE' }); qc.invalidateQueries({ queryKey: ['channels'] }); }} />
              </Space>
            ),
          },
        ]} />
      <Modal open={groupOpen} title="Добавить бота в группу Telegram" onCancel={closeGroup} destroyOnClose
        footer={groupLink ? <Button onClick={closeGroup}>Готово</Button> : undefined} onOk={linkGroup} okText="Открыть Telegram">
        {groupLink ? (
          <Typography.Paragraph>
            В Telegram выберите группу и подтвердите добавление бота. Группа появится в списке сама.
            Если Telegram не открылся — <a href={groupLink} target="_blank" rel="noopener noreferrer">откройте ссылку</a>.
            Ссылка одноразовая и действует 15 минут.
          </Typography.Paragraph>
        ) : (
          <Form form={groupForm} layout="vertical">{severityAndDigest}</Form>
        )}
      </Modal>
      <Modal open={open} title="Чат Telegram по номеру" onOk={add} onCancel={() => setOpen(false)} okText="Добавить" destroyOnClose>
        <Form form={form} layout="vertical">
          <Form.Item name="title" label="Название"><Input placeholder="например, Дежурные" /></Form.Item>
          <Form.Item name="target" label="Номер чата (chat id)" rules={[{ required: true }]} extra="Бот должен уже быть в этом чате.">
            <Input placeholder="-1001234567890" />
          </Form.Item>
          {severityAndDigest}
        </Form>
      </Modal>
    </>
  );
}

/** «Контроль → Telegram»: the user's own chat for everyone; the organization's chats for owners. */
export function Telegram() {
  const me = useMe();
  return (
    <>
      <Card size="small" title="Мой Telegram" style={{ marginBottom: 16, maxWidth: 720 }}>
        <TelegramPersonal />
      </Card>
      {isOwner(me) && (
        <>
          <Typography.Title level={5}>Чаты и группы организации</Typography.Title>
          <Notifications />
        </>
      )}
    </>
  );
}

/** «Контроль»: events, rules (owners), Telegram. */
export default function ControlPage() {
  return <Section section={section('/control')} panes={{ alerts: () => <Alerts />, rules: () => <Rules />, telegram: () => <Telegram /> }} />;
}

