import { CheckOutlined, CloseOutlined, StopOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, App, Button, Card, Col, Descriptions, Empty, Form, Input, Modal, Radio, Row, Select, Space, Table, Tag, Typography } from 'antd';
import { useState } from 'react';
import { Link } from 'react-router-dom';
import { api, isSuper, useMeta } from '../api/client';
import { useMe } from '../auth/auth';
import { QueryState } from '../components/ui';
import { dateTime } from '../lib/format';

interface TopazInfo { last_session_id?: number; last_session_num?: number; last_session_start?: string; last_session_operator?: string; fuels?: string[]; server_version?: string }
interface Request {
  id: string; station_name: string; machine_name: string | null; agent_version: string | null; os: string | null; rid: string | null; ip: string | null;
  topaz_info: string | null; status: string; created_at: string; last_poll_at: string; poll_count: number; decided_by: string | null; decided_at: string | null;
  note: string | null; station_id: string | null; station: string | null; previous_station_id: string | null; previous_station: string | null;
  previous_station_status: string | null; ip_blocked: boolean;
}
interface Data { can_decide: boolean; pending: Request[]; history: Request[]; blocks: { ip: string; until: string; blocked_by: string | null; reason: string | null }[] }

const STATUS: Record<string, { color: string; label: string }> = {
  approved: { color: 'blue', label: 'одобрена, ждёт агента' },
  claimed: { color: 'green', label: 'подключена' },
  rejected: { color: 'red', label: 'отклонена' },
  expired: { color: 'default', label: 'истекла' },
};

const parseTopaz = (s: string | null): TopazInfo | null => {
  try { return s ? (JSON.parse(s) as TopazInfo) : null; } catch { return null; }
};

function ago(iso: string) {
  const s = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
  return s < 90 ? `${s} с назад` : s < 5400 ? `${Math.round(s / 60)} мин назад` : `${Math.round(s / 3600)} ч назад`;
}

/** New agents asking to be connected: a superadmin approves them after comparing the verification code. */
export default function StationRequests() {
  const qc = useQueryClient();
  const { message } = App.useApp();
  const meta = useMeta();
  const me = useMe();
  const { data, error, isLoading } = useQuery({ queryKey: ['registrations'], queryFn: () => api<Data>('/registrations'), refetchInterval: 15_000 });
  const [approving, setApproving] = useState<Request | null>(null);
  const [rejecting, setRejecting] = useState<Request | null>(null);
  const [form] = Form.useForm();
  const [rejectForm] = Form.useForm();
  const mode = Form.useWatch('mode', form);
  const refresh = () => qc.invalidateQueries();

  const approve = async () => {
    const v = await form.validateFields();
    try {
      await api(`/registrations/${approving!.id}/approve`, { method: 'POST', body: { code: v.code, ...(v.mode === 'existing' ? { station_id: v.station_id } : { station_name: v.station_name, organization_id: v.organization_id }) } });
      message.success('АЗС подключена. Агент получит доступ в течение минуты и отправит накопленные данные.');
      setApproving(null);
      refresh();
    } catch (e) {
      message.error((e as Error).message);
    }
  };
  const reject = async () => {
    const v = await rejectForm.validateFields();
    try {
      await api(`/registrations/${rejecting!.id}/reject`, { method: 'POST', body: v });
      message.success(v.block_ip ? 'Заявка отклонена, адрес заблокирован на 30 дней' : 'Заявка отклонена');
      setRejecting(null);
      refresh();
    } catch (e) {
      message.error((e as Error).message);
    }
  };

  return (
    <QueryState error={error} loading={isLoading}>
      {data && (
        <>
          <Typography.Paragraph type="secondary">
            Агент после установки сам присылает заявку на подключение. Одобряйте только после сверки <b>кода подтверждения</b>: установщик видит его в конце установки
            (или в выводе <code>TopazAgent.exe check</code>). Так заявку с чужого компьютера не получится выдать за вашу АЗС. До одобрения агент копит данные у себя и потом отправит всё.
          </Typography.Paragraph>
          {!data.can_decide && data.pending.length > 0 && <Alert type="info" showIcon style={{ marginBottom: 12 }} message="Одобряет или отклоняет заявки суперадминистратор." />}
          {data.pending.length === 0 && <Empty description="Новых заявок нет" style={{ margin: '24px 0' }} />}
          <Row gutter={[12, 12]}>
            {data.pending.map((r) => {
              const t = parseTopaz(r.topaz_info);
              const online = Date.now() - new Date(r.last_poll_at).getTime() < 5 * 60_000;
              return (
                <Col key={r.id} xs={24} lg={12}>
                  <Card size="small" title={<>{r.station_name} {r.previous_station && <Tag color={r.previous_station_status === 'revoked' ? 'red' : 'blue'}>повторное подключение: {r.previous_station}{r.previous_station_status === 'revoked' ? ' (заблокирована)' : ''}</Tag>}</>}
                    extra={<Tag color={online ? 'green' : 'default'}>{online ? 'агент ждёт' : 'не на связи'} · {ago(r.last_poll_at)}</Tag>}>
                    <Descriptions size="small" column={1} items={[
                      { key: 'pc', label: 'Компьютер', children: <>{r.machine_name ?? '—'} · IP {r.ip ?? '—'}{r.ip_blocked && <Tag color="red" style={{ marginInlineStart: 6 }}>адрес заблокирован</Tag>}</> },
                      { key: 'agent', label: 'Агент', children: `${r.agent_version ?? '?'} · ${r.rid ?? ''} · ${r.os ?? ''}` },
                      { key: 'topaz', label: 'Топаз', children: t ? <div>смена №{t.last_session_num ?? t.last_session_id ?? '?'}{t.last_session_operator ? `, оператор ${t.last_session_operator}` : ''}{t.last_session_start ? `, начата ${dateTime(t.last_session_start)}` : ''}{t.fuels?.length ? <div>топливо: {t.fuels.join(', ')}</div> : null}</div> : 'нет сведений' },
                      { key: 'at', label: 'Заявка', children: dateTime(r.created_at) },
                    ]} />
                    {data.can_decide && (
                      <Space wrap style={{ marginTop: 8 }}>
                        <Button type="primary" icon={<CheckOutlined />} onClick={() => {
                          setApproving(r);
                          form.setFieldsValue({ code: '', mode: r.previous_station_id ? 'existing' : 'new', station_name: r.station_name, station_id: r.previous_station_id ?? undefined, organization_id: me.organization_id ?? undefined });
                        }}>Одобрить…</Button>
                        <Button icon={<CloseOutlined />} onClick={() => { setRejecting(r); rejectForm.setFieldsValue({ block_ip: false, note: '' }); }}>Отклонить…</Button>
                      </Space>
                    )}
                  </Card>
                </Col>
              );
            })}
          </Row>

          <Typography.Title level={5} style={{ marginTop: 24 }}>Решения за 30 дней</Typography.Title>
          <Table<Request> size="small" rowKey="id" dataSource={data.history} pagination={{ pageSize: 20 }} scroll={{ x: 800 }} locale={{ emptyText: 'Пока нет' }}
            columns={[
              { title: 'Заявка', key: 'n', render: (_, r) => <>{r.station_name}<div style={{ fontSize: 12, opacity: 0.6 }}>{r.machine_name} · {r.ip}</div></> },
              { title: 'Статус', dataIndex: 'status', render: (v: string) => <Tag color={STATUS[v]?.color}>{STATUS[v]?.label ?? v}</Tag> },
              { title: 'АЗС', dataIndex: 'station', render: (v) => v ?? '—' },
              { title: 'Решение', key: 'd', render: (_, r) => (r.decided_by ? <>{r.decided_by}, {dateTime(r.decided_at)}{r.note && <div style={{ fontSize: 12, opacity: 0.7 }}>{r.note}</div>}</> : '—') },
            ]} />
          {data.blocks.length > 0 && (
            <Typography.Paragraph type="secondary" style={{ marginTop: 12 }}>
              Заблокированные адреса: {data.blocks.map((b) => `${b.ip} до ${dateTime(b.until)}`).join('; ')}
            </Typography.Paragraph>
          )}

          <Modal open={!!approving} title={`Подключить: ${approving?.station_name}`} okText="Подключить" cancelText="Отмена" onOk={approve} onCancel={() => setApproving(null)} destroyOnClose>
            <Form form={form} layout="vertical">
              <Form.Item name="code" label="Код подтверждения с АЗС" extra="Спросите код у того, кто устанавливал агент: он показан в конце установки. Не берите его из этой заявки — смысл именно в сверке."
                rules={[{ required: true, message: 'Введите код' }, { pattern: /^[0-9A-Za-z]{4}[-\s]?[0-9A-Za-z]{4}$/, message: 'Формат: XXXX-XXXX' }]}>
                <Input placeholder="XXXX-XXXX" maxLength={9} autoComplete="off" style={{ fontFamily: 'monospace', letterSpacing: 2, textTransform: 'uppercase' }} />
              </Form.Item>
              <Form.Item name="mode" label="Это">
                <Radio.Group options={[{ value: 'new', label: 'новая АЗС' }, { value: 'existing', label: 'существующая АЗС (переустановка, новый ПК)' }]} />
              </Form.Item>
              {mode === 'existing' ? (
                <Form.Item name="station_id" label="Какая АЗС" rules={[{ required: true, message: 'Выберите АЗС' }]}
                  extra="Список — АЗС организации, выбранной в шапке. История АЗС сохранится; прежний доступ перестанет работать, когда новый агент подключится.">
                  <Select showSearch optionFilterProp="label" options={(meta.data?.stations ?? []).map((s) => ({ value: s.id, label: s.name }))} />
                </Form.Item>
              ) : (
                <>
                  <Form.Item name="station_name" label="Название АЗС" rules={[{ required: true, max: 100 }]}><Input /></Form.Item>
                  <Form.Item name="organization_id" label="Организация (хозяин АЗС)" rules={[{ required: true, message: 'Выберите организацию' }]}
                    extra="Данные АЗС увидят только пользователи этой организации.">
                    <Select options={(me.organizations ?? []).filter((o) => o.is_active).map((o) => ({ value: o.id, label: o.name }))} />
                  </Form.Item>
                </>
              )}
            </Form>
          </Modal>
          <Modal open={!!rejecting} title={`Отклонить заявку: ${rejecting?.station_name}`} okText="Отклонить" okButtonProps={{ danger: true }} cancelText="Отмена" onOk={reject} onCancel={() => setRejecting(null)} destroyOnClose>
            <Form form={rejectForm} layout="vertical">
              <Form.Item name="note" label="Комментарий (для журнала)"><Input maxLength={500} /></Form.Item>
              <Form.Item name="block_ip" label="Адрес отправителя">
                <Radio.Group options={[{ value: false, label: 'не блокировать' }, { value: true, label: <><StopOutlined /> заблокировать на 30 дней ({rejecting?.ip})</> }]} />
              </Form.Item>
            </Form>
          </Modal>
        </>
      )}
    </QueryState>
  );
}

/** Overview hint for owners and superadmins: stations waiting to be connected. */
export function PendingStationsBanner() {
  const me = useMe();
  const n = useMeta().data?.pending_registrations ?? 0;
  if (!isSuper(me) || n === 0) return null;
  return (
    <Alert type="warning" showIcon style={{ marginBottom: 12 }}
      message={<>АЗС ожидают подключения: {n}. Агенты уже установлены и копят данные. <Link to="/stations?tab=connect">Открыть заявки</Link></>} />
  );
}
