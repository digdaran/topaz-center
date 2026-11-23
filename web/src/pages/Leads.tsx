import { CopyOutlined, DeleteOutlined, UserAddOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, App, Button, DatePicker, Descriptions, Flex, Form, Input, Modal, Popconfirm, Radio, Segmented, Select, Space, Table, Tag, Typography } from 'antd';
import dayjs from 'dayjs';
import { useState } from 'react';
import { api } from '../api/client';
import { useMe } from '../auth/auth';
import { QueryState } from '../components/ui';
import { contactLink } from '../lib/contact';
import { dateTime } from '../lib/format';
import { clientMessage, loginFromName } from '../lib/leadClient';
import { LEAD_STAGES, REFUSE_REASONS, type LeadStage, type RefuseReason, refusalText } from '../lib/leads';

interface Lead {
  id: number; created_at: string; name: string; contact: string; stations: number | null; city: string | null; comment: string | null;
  page: string | null; notified_at: string | null; stage: LeadStage; stage_at: string | null; stage_by: string | null;
  refuse_reason: RefuseReason | null; refuse_comment: string | null; note: string | null;
  organization_id: string | null; organization: string | null; trial_until: string | null; organization_stations: number;
}
interface ClientForm { mode: 'new' | 'existing'; organization_name?: string; owner_name?: string; owner_login?: string; organization_id?: string }
interface ClientCreated { organization_id: string; organization: string; login: string | null; password: string | null; service_url: string | null }
interface Summary { month: string; total: number; stages: Record<LeadStage, number>; refuse_reasons: Partial<Record<RefuseReason, number>> }

type Show = 'open' | LeadStage | 'all';

/** «Платформа → Заявки с сайта»: requests left on azshub.ru, from the first call to a connected client or a refusal. */
export default function Leads() {
  const qc = useQueryClient();
  const me = useMe();
  const { message } = App.useApp();
  const [clientFor, setClientFor] = useState<Lead | null>(null);
  const [created, setCreated] = useState<ClientCreated | null>(null);
  const [clientForm] = Form.useForm<ClientForm>();
  const clientMode = Form.useWatch('mode', clientForm);
  const [show, setShow] = useState<Show>('open');
  const [month, setMonth] = useState(dayjs().format('YYYY-MM'));
  const [refusing, setRefusing] = useState<Lead | null>(null);
  const [form] = Form.useForm<{ refuse_reason: RefuseReason; refuse_comment?: string }>();
  const reason = Form.useWatch('refuse_reason', form);
  const list = useQuery({ queryKey: ['leads'], queryFn: () => api<Lead[]>('/leads'), refetchInterval: 60_000 });
  const summary = useQuery({ queryKey: ['leads', 'summary', month], queryFn: () => api<Summary>(`/leads/summary?month=${month}`) });
  const rows = (list.data ?? []).filter((l) => show === 'all' || (show === 'open' ? l.stage === 'new' || l.stage === 'work' : l.stage === show));

  const refresh = () => {
    qc.invalidateQueries({ queryKey: ['leads'] });
    qc.invalidateQueries({ queryKey: ['meta'] });
  };
  const openClient = (l: Lead) => {
    clientForm.setFieldsValue({ mode: 'new', organization_name: l.name, owner_name: l.name, owner_login: loginFromName(l.name), organization_id: undefined });
    setClientFor(l);
  };
  const createClient = async () => {
    const v = await clientForm.validateFields();
    try {
      const r = await api<ClientCreated>(`/leads/${clientFor!.id}/client`, { method: 'POST', body: v });
      setClientFor(null);
      refresh();
      qc.invalidateQueries({ queryKey: ['me'] }); // the new organization in the header and in the station approval
      qc.invalidateQueries({ queryKey: ['organizations'] });
      if (r.password) setCreated(r);
      else message.success(`Заявка привязана к «${r.organization}»`);
    } catch (e) {
      message.error((e as Error).message);
    }
  };
  const message_ = created?.password && created.login
    ? clientMessage({ serviceUrl: created.service_url ?? window.location.origin, login: created.login, password: created.password }) : '';
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(message_);
      message.success('Текст скопирован');
    } catch {
      message.error('Не удалось скопировать — выделите текст и скопируйте вручную');
    }
  };
  const update = async (l: Lead, body: object) => {
    try {
      await api(`/leads/${l.id}`, { method: 'PATCH', body });
      refresh();
      return true;
    } catch (e) {
      message.error((e as Error).message);
      return false;
    }
  };
  const remove = async (l: Lead) => {
    try {
      await api(`/leads/${l.id}`, { method: 'DELETE' });
      message.success(`Заявка №${l.id} удалена`);
      refresh();
    } catch (e) {
      message.error((e as Error).message);
    }
  };
  const refuse = async () => {
    const v = await form.validateFields();
    if (await update(refusing!, { stage: 'refused', ...v })) setRefusing(null);
  };
  const s = summary.data;

  return (
    <>
      <Typography.Paragraph type="secondary">
        Заявки с сайта azshub.ru. Новая заявка сразу приходит в Telegram администраторам сервиса; если за сутки её не взяли в работу, бот напомнит.
        Отказы удаляются через год, остальные заявки — через три года.
      </Typography.Paragraph>

      <Flex wrap gap={12} align="center" style={{ marginBottom: 16 }}>
        <DatePicker picker="month" allowClear={false} format="MMMM YYYY" value={dayjs(month + '-01')} onChange={(d) => d && setMonth(d.format('YYYY-MM'))} />
        {s && (
          <Typography.Text>
            Заявок: <b>{s.total}</b>
            {LEAD_STAGES.map((st) => <span key={st.value}> · {st.label}: <b>{s.stages[st.value] ?? 0}</b></span>)}
            {Object.keys(s.refuse_reasons).length > 0 && (
              <Typography.Text type="secondary">
                {' '}({REFUSE_REASONS.filter((r) => s.refuse_reasons[r.value]).map((r) => `${r.label.toLowerCase()} — ${s.refuse_reasons[r.value]}`).join(', ')})
              </Typography.Text>
            )}
          </Typography.Text>
        )}
      </Flex>

      <Segmented value={show} onChange={(v) => setShow(v as Show)} style={{ marginBottom: 12 }}
        options={[{ value: 'open', label: 'Новые и в работе' }, { value: 'connected', label: 'Подключены' }, { value: 'refused', label: 'Отказы' }, { value: 'all', label: 'Все' }]} />

      <QueryState error={list.error} loading={list.isLoading}>
        <Table<Lead> size="small" rowKey="id" dataSource={rows} pagination={{ pageSize: 50 }} scroll={{ x: 1300 }}
          locale={{ emptyText: show === 'open' ? 'Необработанных заявок нет' : 'Заявок нет' }}
          columns={[
            { title: '№', dataIndex: 'id', width: 60 },
            { title: 'Когда', dataIndex: 'created_at', width: 130, render: (v: string) => dateTime(v) },
            {
              title: 'Кто', key: 'who', width: 230,
              render: (_, l) => {
                const href = contactLink(l.contact);
                return (
                  <>
                    <div><b>{l.name}</b></div>
                    {href ? <a href={href} target="_blank" rel="noreferrer">{l.contact}</a> : l.contact}
                    <div><Typography.Text type="secondary">{[l.stations ? `АЗС: ${l.stations}` : null, l.city].filter(Boolean).join(', ')}</Typography.Text></div>
                  </>
                );
              },
            },
            {
              title: 'Из заявки', key: 'comment',
              render: (_, l) => (
                <>
                  {l.comment && <div style={{ whiteSpace: 'pre-wrap' }}>{l.comment}</div>}
                  {l.page && l.page !== '/' && <Typography.Text type="secondary" style={{ fontSize: 12 }}>{l.page}</Typography.Text>}
                </>
              ),
            },
            {
              title: 'Заметка', dataIndex: 'note', width: 220,
              render: (v: string | null, l) => (
                <Typography.Text type={v ? undefined : 'secondary'}
                  editable={{ text: v ?? '', triggerType: ['icon', 'text'], maxLength: 2000, onChange: (n) => { if (n.trim() !== (v ?? '')) update(l, { note: n.trim() }); } }}>
                  {v ?? 'добавить'}
                </Typography.Text>
              ),
            },
            {
              title: 'Этап', key: 'stage', width: 220,
              render: (_, l) => (
                <Space direction="vertical" size={2}>
                  <Select size="small" style={{ width: 150 }} value={l.stage}
                    options={LEAD_STAGES.map((st) => ({ value: st.value, label: <Tag color={st.color} style={{ marginInlineEnd: 0 }}>{st.label}</Tag> }))}
                    onChange={(stage: LeadStage) => {
                      if (stage === 'refused') { form.resetFields(); setRefusing(l); } else update(l, { stage });
                    }} />
                  {l.stage === 'refused' && <Typography.Text type="secondary" style={{ fontSize: 12 }}>{refusalText(l.refuse_reason, l.refuse_comment)}</Typography.Text>}
                  {l.stage_by && <Typography.Text type="secondary" style={{ fontSize: 12 }}>{l.stage_by}, {dateTime(l.stage_at)}</Typography.Text>}
                </Space>
              ),
            },
            {
              title: 'Клиент', key: 'client', width: 200,
              render: (_, l) => l.organization ? (
                <>
                  <div><b>{l.organization}</b></div>
                  <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                    {l.organization_stations > 0 ? `АЗС: ${l.organization_stations}` : 'АЗС ещё не подключены'}
                    {l.trial_until && `, пробный до ${dayjs(l.trial_until).format('DD.MM.YYYY')}`}
                  </Typography.Text>
                </>
              ) : (
                <Button size="small" icon={<UserAddOutlined />} onClick={() => openClient(l)}>Создать клиента</Button>
              ),
            },
            {
              title: '', key: 'delete', width: 50,
              render: (_, l) => (
                <Popconfirm title={`Удалить заявку №${l.id}?`}
                  description={l.organization ? `Имя и контакт будут стёрты без возможности восстановить. Организация «${l.organization}» останется.` : 'Имя и контакт будут стёрты без возможности восстановить.'}
                  okText="Удалить" okButtonProps={{ danger: true }} cancelText="Отмена"
                  onConfirm={() => remove(l)}>
                  <Button size="small" type="text" danger icon={<DeleteOutlined />} aria-label={`Удалить заявку №${l.id}`} />
                </Popconfirm>
              ),
            },
          ]} />
      </QueryState>

      <Modal open={!!refusing} title={refusing ? `Отказ по заявке №${refusing.id}` : ''} okText="Сохранить" cancelText="Отмена"
        onOk={refuse} onCancel={() => setRefusing(null)} destroyOnClose>
        <Form form={form} layout="vertical">
          <Form.Item name="refuse_reason" label="Причина" rules={[{ required: true, message: 'Выберите причину' }]}>
            <Select options={REFUSE_REASONS.map((r) => ({ value: r.value, label: r.label }))} />
          </Form.Item>
          <Form.Item name="refuse_comment" label="Подробнее" rules={[{ required: reason === 'other', whitespace: true, message: 'Опишите причину' }]}>
            <Input maxLength={300} />
          </Form.Item>
        </Form>
      </Modal>

      <Modal open={!!clientFor} title={clientFor ? `Клиент из заявки №${clientFor.id}` : ''} okText={clientMode === 'existing' ? 'Привязать' : 'Создать'} cancelText="Отмена"
        onOk={createClient} onCancel={() => setClientFor(null)} destroyOnClose>
        <Form form={clientForm} layout="vertical">
          <Form.Item name="mode">
            <Radio.Group optionType="button" options={[{ value: 'new', label: 'Новая организация' }, { value: 'existing', label: 'Уже наш клиент' }]} />
          </Form.Item>
          {clientMode === 'existing' ? (
            <Form.Item name="organization_id" label="Организация" rules={[{ required: true, message: 'Выберите организацию' }]}
              extra="Новый владелец не создаётся: клиент подключает ещё одну АЗС к своей организации.">
              <Select showSearch optionFilterProp="label" options={(me.organizations ?? []).filter((o) => o.is_active).map((o) => ({ value: o.id, label: o.name }))} />
            </Form.Item>
          ) : (
            <>
              <Form.Item name="organization_name" label="Название организации" rules={[{ required: true, whitespace: true, max: 100 }]}
                extra="Как клиент называет свою сеть, например «ИП Петров» или «АЗС Восток».">
                <Input />
              </Form.Item>
              <Form.Item name="owner_name" label="Имя владельца" rules={[{ max: 100 }]}><Input /></Form.Item>
              <Form.Item name="owner_login" label="Логин владельца" rules={[{ required: true, min: 3, max: 50 }, { pattern: /^\S+$/, message: 'Без пробелов' }]}
                extra="Временный пароль сервис придумает сам и покажет один раз.">
                <Input />
              </Form.Item>
            </>
          )}
        </Form>
      </Modal>

      <Modal open={!!created} title={`Клиент «${created?.organization ?? ''}» создан`} onCancel={() => setCreated(null)} width={640}
        footer={[<Button key="copy" icon={<CopyOutlined />} onClick={copy}>Скопировать текст</Button>, <Button key="ok" type="primary" onClick={() => setCreated(null)}>Готово</Button>]}>
        <Alert type="warning" showIcon style={{ marginBottom: 12 }}
          message="Пароль показывается только сейчас" description="Отправьте текст клиенту. Если потеряете пароль, задайте новый в «Пользователях»." />
        <Descriptions size="small" column={1} style={{ marginBottom: 12 }}
          items={[{ key: 'l', label: 'Логин', children: <Typography.Text copyable>{created?.login}</Typography.Text> },
                  { key: 'p', label: 'Временный пароль', children: <Typography.Text copyable code>{created?.password}</Typography.Text> }]} />
        <Input.TextArea readOnly value={message_} autoSize={{ minRows: 8, maxRows: 16 }} />
      </Modal>
    </>
  );
}
