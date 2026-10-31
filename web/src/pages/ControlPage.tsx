import { DeleteOutlined, ReloadOutlined, SendOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, App, Button, Card, Col, Form, Input, InputNumber, Modal, Row, Segmented, Select, Space, Switch, Table, Tabs, Tag, Typography } from 'antd';
import { useState } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { api, useFiltered } from '../api/client';
import { useMe } from '../auth/auth';
import { FilterBar } from '../components/FilterBar';
import { PageTitle, QueryState } from '../components/ui';
import { dateTime } from '../lib/format';

type R = Record<string, any>;
export const SEVERITY: Record<string, { color: string; label: string }> = {
  critical: { color: 'red', label: 'критично' },
  warning: { color: 'orange', label: 'внимание' },
  info: { color: 'blue', label: 'информация' },
};

function Alerts() {
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
  return (
    <>
      <FilterBar granularity={false} compare={false} />
      <Space wrap style={{ marginBottom: 12 }}>
        <Segmented value={status} onChange={(v) => setStatus(String(v))} options={[{ value: 'open', label: 'Открытые' }, { value: 'new', label: 'Новые' }, { value: 'closed', label: 'Закрытые' }, { value: 'all', label: 'Все' }]} />
        <Select allowClear placeholder="Важность" style={{ width: 150 }} value={severity} onChange={setSeverity} options={Object.entries(SEVERITY).map(([k, v]) => ({ value: k, label: v.label }))} />
        <Select allowClear placeholder="Правило" style={{ width: 300 }} value={rule} onChange={setRule} options={(data?.by_rule ?? []).map((r) => ({ value: r.rule_code, label: `${r.title} (${r.total})` }))} />
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

function Rules() {
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
  const run = async () => {
    const r = await api<{ found: number }>('/alerts/run', { method: 'POST' });
    message.success(`Проверка выполнена, новых событий: ${r.found}`);
    qc.invalidateQueries();
  };
  return (
    <>
      <Typography.Paragraph type="secondary">Правила проверяются каждые 5 минут по данным за последние 30 дней. Одно событие на объект (смену, рукав, приём) не повторяется.</Typography.Paragraph>
      <Button icon={<ReloadOutlined />} onClick={run} style={{ marginBottom: 12 }}>Проверить сейчас</Button>
      <Table size="small" rowKey="code" loading={isLoading} dataSource={data} pagination={false} scroll={{ x: 900 }}
        columns={[
          { title: 'Вкл.', dataIndex: 'enabled', width: 60, render: (v: boolean, r) => <Switch size="small" checked={v} onChange={(c) => save(r, { enabled: c })} /> },
          { title: 'Правило', dataIndex: 'title', render: (v, r) => <><b>{v}</b><div style={{ fontSize: 13, opacity: 0.75 }}>{r.description}</div></> },
          {
            title: 'Порог', dataIndex: 'threshold', width: 150, render: (v: number | null, r) => (v == null ? '—' : (
              <InputNumber size="small" defaultValue={Number(v)} min={0} addonAfter={r.unit} style={{ width: 130 }} onBlur={(e) => { const n = Number(e.target.value.replace(',', '.')); if (n !== Number(v)) save(r, { threshold: n }); }} />
            )),
          },
          {
            title: 'Важность', dataIndex: 'severity', width: 150, render: (v: string, r) => (
              <Select size="small" value={v} style={{ width: 130 }} onChange={(s) => save(r, { severity: s })} options={Object.entries(SEVERITY).map(([k, x]) => ({ value: k, label: x.label }))} />
            ),
          },
        ]} />
    </>
  );
}

function Notifications() {
  const qc = useQueryClient();
  const { message } = App.useApp();
  const { data, isLoading } = useQuery({ queryKey: ['channels'], queryFn: () => api<{ token_configured: boolean; channels: R[] }>('/notifications') });
  const [open, setOpen] = useState(false);
  const [form] = Form.useForm();
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
  const test = async (id: number) => {
    try {
      await api(`/notifications/${id}/test`, { method: 'POST' });
      message.success('Тестовое сообщение отправлено');
    } catch (e) {
      message.error((e as Error).message);
    }
  };
  return (
    <>
      {data && !data.token_configured && (
        <Alert type="warning" showIcon style={{ marginBottom: 12 }} message="Токен Telegram-бота не задан"
          description={<>Создайте бота у <code>@BotFather</code>, впишите токен в <code>TOPAZ_TELEGRAM_BOT_TOKEN</code> в файле <code>.env</code> сервера и перезапустите сервис.</>} />
      )}
      <Typography.Paragraph type="secondary">
        Сразу приходят события с важностью не ниже выбранной (только за последние сутки); утром в 9:00 по Москве — сводка за сутки.
        Chat id: добавьте бота в чат и откройте <code>https://api.telegram.org/bot&lt;токен&gt;/getUpdates</code>, либо используйте бота @userinfobot для личного чата.
      </Typography.Paragraph>
      <Button type="primary" onClick={() => { setOpen(true); form.setFieldsValue({ min_severity: 'warning', daily_digest: true }); }} style={{ marginBottom: 12 }}>Добавить чат</Button>
      <Table size="small" rowKey="id" loading={isLoading} dataSource={data?.channels} pagination={false}
        columns={[
          { title: 'Чат', dataIndex: 'title', render: (v, r) => <>{v ?? '—'} <Typography.Text type="secondary">{r.target}</Typography.Text></> },
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
      <Modal open={open} title="Чат Telegram для уведомлений" onOk={add} onCancel={() => setOpen(false)} okText="Добавить" destroyOnClose>
        <Form form={form} layout="vertical">
          <Form.Item name="title" label="Название"><Input placeholder="например, Дежурные" /></Form.Item>
          <Form.Item name="target" label="Chat id" rules={[{ required: true }]}><Input placeholder="-1001234567890" /></Form.Item>
          <Form.Item name="min_severity" label="Присылать события с важностью от">
            <Select options={Object.entries(SEVERITY).map(([k, v]) => ({ value: k, label: v.label }))} />
          </Form.Item>
          <Form.Item name="daily_digest" label="Утренняя сводка" valuePropName="checked"><Switch /></Form.Item>
        </Form>
      </Modal>
    </>
  );
}

export default function ControlPage() {
  const me = useMe();
  return (
    <>
      <PageTitle title="Контроль" />
      <Tabs destroyInactiveTabPane items={[
        { key: 'alerts', label: 'События', children: <Alerts /> },
        ...(me.role === 'owner' ? [
          { key: 'rules', label: 'Правила', children: <Rules /> },
          { key: 'notify', label: 'Уведомления в Telegram', children: <Notifications /> },
        ] : []),
      ]} />
    </>
  );
}

/** Latest new alerts for the overview page. */
export function LatestAlerts() {
  const { data } = useQuery({ queryKey: ['alerts-summary'], queryFn: () => api<{ counts: R; latest: R[] }>('/alerts/summary'), refetchInterval: 60_000 });
  const location = useLocation();
  if (!data || data.latest.length === 0) return null;
  return (
    <Card size="small" style={{ marginTop: 12 }} title={<>Новые события <Tag color={data.counts.critical > 0 ? 'red' : 'orange'}>{data.counts.new}</Tag></>}
      extra={<Link to={`/control${location.search}`}>все события</Link>}>
      <Row gutter={[8, 8]}>
        {data.latest.map((a) => (
          <Col key={a.id} xs={24} md={12}>
            <Tag color={SEVERITY[a.severity]?.color}>{SEVERITY[a.severity]?.label}</Tag> <b>{a.station}</b>: {a.title}
            {a.session_id != null && <> · <Link to={`/shifts/${a.station_id}/${a.session_id}`}>смена</Link></>}
          </Col>
        ))}
      </Row>
    </Card>
  );
}
