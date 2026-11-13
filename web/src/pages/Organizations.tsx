import { PlusOutlined, SwapOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { App, Button, Form, Input, Modal, Select, Space, Switch, Table, Tag, Typography } from 'antd';
import { useState } from 'react';
import { api } from '../api/client';
import { dateTime, int } from '../lib/format';

interface Org {
  id: string; name: string; is_active: boolean; created_at: string; stations: number; users: number; owners: number;
  station_list: { id: string; name: string }[];
}

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
  const [moving, setMoving] = useState(false);
  const [moveForm] = Form.useForm<{ station: string; target: string }>();
  const station = Form.useWatch('station', moveForm);
  const owner = data?.find((o) => o.station_list.some((s) => s.id === station));

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
        Новую АЗС вы относите к организации при одобрении заявки (Платформа → Подключение АЗС). Данные любой организации вы смотрите, выбрав её в шапке.
      </Typography.Paragraph>
      <Space wrap style={{ marginBottom: 12 }}>
        <Button type="primary" icon={<PlusOutlined />} onClick={() => { setName(''); setAdding(true); }}>Добавить организацию</Button>
        <Button icon={<SwapOutlined />} disabled={(data?.length ?? 0) < 2} onClick={() => { moveForm.resetFields(); setMoving(true); }}
          title={(data?.length ?? 0) < 2 ? 'Сначала добавьте вторую организацию' : undefined}>Передать АЗС другой организации</Button>
      </Space>
      <Table<Org> size="small" rowKey="id" loading={isLoading} dataSource={data} pagination={false} scroll={{ x: 700 }}
        columns={[
          {
            title: 'Название', dataIndex: 'name', render: (v: string, o) => (
              <Typography.Text editable={{ onChange: (n) => { if (n.trim() && n.trim() !== v) update(o, { name: n.trim() }); }, triggerType: ['icon', 'text'] }}>{v}</Typography.Text>
            ),
          },
          {
            title: 'АЗС', dataIndex: 'stations', render: (v: number, o) => (v === 0 ? <Typography.Text type="secondary">нет</Typography.Text>
              : <Space size={[4, 4]} wrap>{o.station_list.map((s) => <Tag key={s.id} style={{ marginInlineEnd: 0 }}>{s.name}</Tag>)}</Space>),
          },
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
      <Modal open={moving} title="Передать АЗС другой организации" okText="Передать" cancelText="Отмена" onCancel={() => setMoving(false)} destroyOnClose
        onOk={async () => {
          const v = await moveForm.validateFields();
          if (await call(() => api(`/stations/${v.station}/organization`, { method: 'POST', body: { organization_id: v.target } }), 'АЗС передана')) setMoving(false);
        }}>
        <Typography.Paragraph type="secondary">
          Вся история АЗС переходит к новой организации. Управляющие и наблюдатели прежней организации теряют к ней доступ, её владелец перестаёт её видеть.
        </Typography.Paragraph>
        <Form form={moveForm} layout="vertical">
          <Form.Item name="station" label="АЗС" rules={[{ required: true, message: 'Выберите АЗС' }]}>
            <Select showSearch optionFilterProp="label" placeholder="АЗС"
              options={(data ?? []).filter((o) => o.station_list.length > 0).map((o) => ({ label: o.name, title: o.name, options: o.station_list.map((s) => ({ value: s.id, label: s.name })) }))} />
          </Form.Item>
          <Form.Item name="target" label="Новая организация" rules={[{ required: true, message: 'Выберите организацию' }]}>
            <Select placeholder="Организация" options={(data ?? []).filter((o) => o.id !== owner?.id).map((o) => ({ value: o.id, label: o.is_active ? o.name : `${o.name} (отключена)` }))} />
          </Form.Item>
        </Form>
      </Modal>
      <Modal open={adding} title="Новая организация" okText="Добавить" cancelText="Отмена" onCancel={() => setAdding(false)} okButtonProps={{ disabled: !name.trim() }}
        onOk={async () => { if (await call(() => api('/organizations', { method: 'POST', body: { name: name.trim() } }), 'Организация добавлена. Создайте её владельца в «Пользователях».')) setAdding(false); }}>
        <Input autoFocus placeholder="например, ООО «Северный путь»" value={name} maxLength={100} onChange={(e) => setName(e.target.value)} />
      </Modal>
    </>
  );
}
