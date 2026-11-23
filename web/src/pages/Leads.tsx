import { CheckOutlined, UndoOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { App, Button, Segmented, Table, Tag, Typography } from 'antd';
import { useState } from 'react';
import { api } from '../api/client';
import { QueryState } from '../components/ui';
import { dateTime } from '../lib/format';
import { contactLink } from '../lib/contact';

interface Lead {
  id: number; created_at: string; name: string; contact: string; stations: number | null; city: string | null; comment: string | null;
  page: string | null; notified_at: string | null; handled_at: string | null; handled_by: string | null;
}

/** «Платформа → Заявки с сайта»: requests left on azshub.ru; a superadmin marks them handled after calling back. */
export default function Leads() {
  const qc = useQueryClient();
  const { message } = App.useApp();
  const [show, setShow] = useState<'new' | 'all'>('new');
  const { data, error, isLoading } = useQuery({ queryKey: ['leads'], queryFn: () => api<Lead[]>('/leads'), refetchInterval: 60_000 });
  const rows = (data ?? []).filter((l) => show === 'all' || !l.handled_at);
  const setHandled = async (l: Lead, handled: boolean) => {
    try {
      await api(`/leads/${l.id}/status`, { method: 'POST', body: { handled } });
      qc.invalidateQueries({ queryKey: ['leads'] });
      qc.invalidateQueries({ queryKey: ['meta'] });
    } catch (e) {
      message.error((e as Error).message);
    }
  };

  return (
    <>
      <Typography.Paragraph type="secondary">
        Заявки, оставленные на сайте azshub.ru. Каждая новая заявка сразу приходит и в Telegram администраторам сервиса.
        Когда перезвонили, нажмите «Обработана» — заявка уйдёт из новых.
      </Typography.Paragraph>
      <Segmented value={show} onChange={(v) => setShow(v as 'new' | 'all')} style={{ marginBottom: 12 }}
        options={[{ value: 'new', label: 'Новые' }, { value: 'all', label: 'Все' }]} />
      <QueryState error={error} loading={isLoading}>
        <Table<Lead> size="small" rowKey="id" dataSource={rows} pagination={{ pageSize: 50 }} scroll={{ x: 900 }}
          locale={{ emptyText: show === 'new' ? 'Новых заявок нет' : 'Заявок пока нет' }}
          columns={[
            { title: 'Когда', dataIndex: 'created_at', width: 140, render: (v: string) => dateTime(v) },
            { title: 'Имя', dataIndex: 'name', width: 180 },
            {
              title: 'Связь', dataIndex: 'contact', width: 200,
              render: (v: string) => { const href = contactLink(v); return href ? <a href={href} target="_blank" rel="noreferrer">{v}</a> : v; },
            },
            { title: 'АЗС', dataIndex: 'stations', width: 70, align: 'right', render: (v: number | null) => v ?? '—' },
            { title: 'Город', dataIndex: 'city', width: 130, render: (v: string | null) => v ?? '—' },
            {
              title: 'Комментарий', key: 'comment',
              render: (_, l) => (
                <>
                  {l.comment && <div style={{ whiteSpace: 'pre-wrap' }}>{l.comment}</div>}
                  {l.page && l.page !== '/' && <Typography.Text type="secondary" style={{ fontSize: 12 }}>{l.page}</Typography.Text>}
                </>
              ),
            },
            {
              title: 'Статус', key: 'status', width: 210,
              render: (_, l) => l.handled_at
                ? (
                  <>
                    <Tag color="green">обработана{l.handled_by ? ` (${l.handled_by})` : ''}</Tag>
                    <Button size="small" type="link" icon={<UndoOutlined />} onClick={() => setHandled(l, false)}>Вернуть</Button>
                  </>
                )
                : (
                  <>
                    <Tag color="gold">новая</Tag>
                    <Button size="small" icon={<CheckOutlined />} onClick={() => setHandled(l, true)}>Обработана</Button>
                  </>
                ),
            },
          ]} />
      </QueryState>
    </>
  );
}
