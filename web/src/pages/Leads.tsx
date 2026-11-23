import { DeleteOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { App, Button, DatePicker, Flex, Form, Input, Modal, Popconfirm, Segmented, Select, Space, Table, Tag, Typography } from 'antd';
import dayjs from 'dayjs';
import { useState } from 'react';
import { api } from '../api/client';
import { QueryState } from '../components/ui';
import { contactLink } from '../lib/contact';
import { dateTime } from '../lib/format';
import { LEAD_STAGES, REFUSE_REASONS, type LeadStage, type RefuseReason, refusalText } from '../lib/leads';

interface Lead {
  id: number; created_at: string; name: string; contact: string; stations: number | null; city: string | null; comment: string | null;
  page: string | null; notified_at: string | null; stage: LeadStage; stage_at: string | null; stage_by: string | null;
  refuse_reason: RefuseReason | null; refuse_comment: string | null; note: string | null;
}
interface Summary { month: string; total: number; stages: Record<LeadStage, number>; refuse_reasons: Partial<Record<RefuseReason, number>> }

type Show = 'open' | LeadStage | 'all';

/** «Платформа → Заявки с сайта»: requests left on azshub.ru, from the first call to a connected client or a refusal. */
export default function Leads() {
  const qc = useQueryClient();
  const { message } = App.useApp();
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
        <Table<Lead> size="small" rowKey="id" dataSource={rows} pagination={{ pageSize: 50 }} scroll={{ x: 1100 }}
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
              title: '', key: 'delete', width: 50,
              render: (_, l) => (
                <Popconfirm title={`Удалить заявку №${l.id}?`} description="Имя и контакт будут стёрты без возможности восстановить." okText="Удалить" okButtonProps={{ danger: true }} cancelText="Отмена"
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
    </>
  );
}
