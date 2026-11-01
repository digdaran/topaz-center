import { PlusOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { App, Button, Input, Modal, Switch, Table, Tag, Typography } from 'antd';
import { useState } from 'react';
import { api } from '../api/client';
import { dateTime, int } from '../lib/format';

interface Org { id: string; name: string; is_active: boolean; created_at: string; stations: number; users: number; owners: number }

/**
 * Organizations (station owners sharing the server; superadmin only). Each sees only its own stations,
 * users, Telegram chats and rule thresholds; new stations are assigned when their connection request is approved.
 */
export default function Organizations() {
  const qc = useQueryClient();
  const { message, modal } = App.useApp();
  const { data, isLoading } = useQuery({ queryKey: ['organizations'], queryFn: () => api<Org[]>('/organizations') });
  const [adding, setAdding] = useState(false);
  const [name, setName] = useState('');

  const call = async (fn: () => Promise<unknown>, ok?: string) => {
    try {
      await fn();
      if (ok) message.success(ok);
      qc.invalidateQueries();
      return true;
    } catch (e) {
      message.error((e as Error).message);
      return false;
    }
  };
  const update = (o: Org, body: Record<string, unknown>) => call(() => api(`/organizations/${o.id}`, { method: 'PATCH', body }));

  return (
    <>
      <Typography.Paragraph type="secondary">
        Каждая организация — отдельный хозяин АЗС: видит только свои АЗС, своих пользователей, свои чаты Telegram и свои пороги правил.
        Новую АЗС вы относите к организации при одобрении заявки (АЗС → Подключение). Данные любой организации вы смотрите, выбрав её в шапке.
      </Typography.Paragraph>
      <Button type="primary" icon={<PlusOutlined />} onClick={() => { setName(''); setAdding(true); }} style={{ marginBottom: 12 }}>Добавить организацию</Button>
      <Table<Org> size="small" rowKey="id" loading={isLoading} dataSource={data} pagination={false} scroll={{ x: 700 }}
        columns={[
          {
            title: 'Название', dataIndex: 'name', render: (v: string, o) => (
              <Typography.Text editable={{ onChange: (n) => { if (n.trim() && n.trim() !== v) update(o, { name: n.trim() }); }, triggerType: ['icon', 'text'] }}>{v}</Typography.Text>
            ),
          },
          { title: 'АЗС', dataIndex: 'stations', width: 80, align: 'right', render: (v) => int(v) },
          { title: 'Пользователи', dataIndex: 'users', width: 120, align: 'right', render: (v, o) => <>{int(v)} {o.owners === 0 && <Tag color="orange">нет владельца</Tag>}</> },
          { title: 'Создана', dataIndex: 'created_at', width: 150, render: (v) => dateTime(v) },
          {
            title: 'Активна', dataIndex: 'is_active', width: 90, render: (v: boolean, o) => (
              <Switch size="small" checked={v} onChange={(c) => (c ? update(o, { is_active: true }) : modal.confirm({
                title: `Отключить «${o.name}»?`,
                content: 'Пользователи организации не смогут войти, их сеансы закроются. АЗС продолжат передавать данные, ничего не удаляется.',
                okText: 'Отключить', okButtonProps: { danger: true }, cancelText: 'Отмена',
                onOk: () => update(o, { is_active: false }),
              }))} />
            ),
          },
        ]} />
      <Modal open={adding} title="Новая организация" okText="Добавить" cancelText="Отмена" onCancel={() => setAdding(false)} okButtonProps={{ disabled: !name.trim() }}
        onOk={async () => { if (await call(() => api('/organizations', { method: 'POST', body: { name: name.trim() } }), 'Организация добавлена. Создайте её владельца в «Пользователях».')) setAdding(false); }}>
        <Input autoFocus placeholder="например, ООО «Северный путь»" value={name} maxLength={100} onChange={(e) => setName(e.target.value)} />
      </Modal>
    </>
  );
}
