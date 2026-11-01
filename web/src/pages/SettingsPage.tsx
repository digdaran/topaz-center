import { PlusOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { App, Badge, Button, Card, Form, Input, Modal, Select, Switch, Table, Tabs, Tag, Typography } from 'antd';
import { useState } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { api, isOwner, isSuper, useMeta } from '../api/client';
import { useMe } from '../auth/auth';
import { PageTitle } from '../components/ui';
import { FuelCatalog } from './FuelCatalog';
import Organizations from './Organizations';
import { OrganizationSettings, PaymentModes } from './OrgSettings';
import { dateTime } from '../lib/format';

interface UserRow { id: string; login: string; name: string; role: string; is_active: boolean; created_at: string; last_login_at: string | null; locked_until: string | null; stations: string[]; organization_id: string | null; organization: string | null }
interface AuditRow { id: number; user_login: string | null; action: string; entity: string | null; details: unknown; at: string; organization?: string | null }

const ROLE_LABEL: Record<string, string> = { superadmin: 'Суперадминистратор', owner: 'Владелец', manager: 'Управляющий', viewer: 'Просмотр' };
const ROLE_HELP: Record<string, string> = {
  superadmin: 'всё, что владелец, плюс подключение АЗС, обновление агентов, пользователи и журнал действий',
  owner: 'все АЗС, деньги, справочники, правила контроля и уведомления',
  manager: 'назначенные АЗС, аналитика с маржой, ввод себестоимости и планов',
  viewer: 'назначенные АЗС, только просмотр, без себестоимости и маржи',
};
const MIN_PASSWORD = 10;

function Users() {
  const qc = useQueryClient();
  const { message } = App.useApp();
  const me = useMe();
  const meta = useMeta();
  const { data, isLoading } = useQuery({ queryKey: ['users'], queryFn: () => api<UserRow[]>('/users') });
  const [edit, setEdit] = useState<UserRow | 'new' | null>(null);
  const [form] = Form.useForm();
  const role = Form.useWatch('role', form);
  const formOrg = Form.useWatch('organization_id', form);
  const sup = isSuper(me);
  // Stations offered are those of the organization on screen (a superadmin switches it in the header).
  const stationsSelectable = !sup || !formOrg || formOrg === me.organization_id;
  const stationName = (id: string) => meta.data?.stations.find((s) => s.id === id)?.name ?? id.slice(0, 8);

  const open = (u: UserRow | 'new') => {
    setEdit(u);
    form.resetFields();
    form.setFieldsValue(u === 'new' ? { role: 'manager', stations: [], organization_id: me.organization_id } : { ...u, password: undefined });
  };
  const save = async () => {
    const v = await form.validateFields();
    try {
      if (edit === 'new') await api('/users', { method: 'POST', body: v });
      else await api(`/users/${(edit as UserRow).id}`, { method: 'PATCH', body: { name: v.name, role: v.role, is_active: v.is_active, stations: stationsSelectable ? v.stations : undefined, password: v.password || null, organization_id: sup ? v.organization_id : undefined } });
      message.success('Сохранено');
      setEdit(null);
      qc.invalidateQueries({ queryKey: ['users'] });
    } catch (e) {
      message.error((e as Error).message);
    }
  };

  return (
    <>
      <Button type="primary" icon={<PlusOutlined />} onClick={() => open('new')} style={{ marginBottom: 12 }}>Добавить пользователя</Button>
      <Table<UserRow>
        size="small" rowKey="id" loading={isLoading} dataSource={data} pagination={false} scroll={{ x: 800 }}
        onRow={(u) => ({ onClick: () => open(u), style: { cursor: 'pointer' } })}
        columns={[
          ...(sup ? [{ title: 'Организация', dataIndex: 'organization', render: (v: string | null) => v ?? <Tag color="blue">платформа</Tag> }] : []),
          { title: 'Логин', dataIndex: 'login' },
          { title: 'Имя', dataIndex: 'name' },
          { title: 'Роль', dataIndex: 'role', render: (v) => ROLE_LABEL[v] },
          { title: 'АЗС', dataIndex: 'stations', render: (v: string[], r) => (r.role === 'owner' || r.role === 'superadmin' ? 'все' : v.length ? v.map((s) => <Tag key={s}>{stationName(s)}</Tag>) : <Tag color="orange">не назначены</Tag>) },
          { title: 'Статус', key: 's', render: (_, r) => (!r.is_active ? <Tag>отключён</Tag> : r.locked_until && new Date(r.locked_until) > new Date() ? <Tag color="red">заблокирован</Tag> : <Tag color="green">активен</Tag>) },
          { title: 'Последний вход', dataIndex: 'last_login_at', render: (v) => dateTime(v) },
        ]}
      />
      <Modal open={!!edit} title={edit === 'new' ? 'Новый пользователь' : `Пользователь ${(edit as UserRow | null)?.login}`} onOk={save} onCancel={() => setEdit(null)} okText="Сохранить" destroyOnClose>
        <Form form={form} layout="vertical">
          {edit === 'new' && <Form.Item name="login" label="Логин" rules={[{ required: true, max: 64 }]}><Input autoComplete="off" /></Form.Item>}
          <Form.Item name="name" label="Имя" rules={[{ required: true }]}><Input /></Form.Item>
          <Form.Item name="role" label="Роль" extra={role && ROLE_HELP[role]}>
            <Select disabled={edit !== 'new' && (edit as UserRow)?.id === me.id}
              options={Object.entries(ROLE_LABEL).filter(([value]) => sup || value !== 'superadmin').map(([value, label]) => ({ value, label }))} />
          </Form.Item>
          {sup && role !== 'superadmin' && (
            <Form.Item name="organization_id" label="Организация" rules={[{ required: true, message: 'Выберите организацию' }]}>
              <Select options={(me.organizations ?? []).map((o) => ({ value: o.id, label: o.name }))} />
            </Form.Item>
          )}
          {role !== 'owner' && role !== 'superadmin' && !stationsSelectable && (
            <Typography.Paragraph type="secondary">Чтобы выдать доступ к АЗС другой организации, переключитесь на неё в шапке.</Typography.Paragraph>
          )}
          {role !== 'owner' && role !== 'superadmin' && stationsSelectable && (
            <Form.Item name="stations" label="Доступ к АЗС">
              <Select mode="multiple" optionFilterProp="label" options={meta.data?.stations.map((s) => ({ value: s.id, label: s.name }))} />
            </Form.Item>
          )}
          <Form.Item name="password" label={edit === 'new' ? 'Пароль' : 'Новый пароль (если нужно сменить)'}
            rules={[{ required: edit === 'new', message: 'Введите пароль' }, { min: MIN_PASSWORD, message: `Не короче ${MIN_PASSWORD} символов` }]}>
            <Input.Password autoComplete="new-password" />
          </Form.Item>
          {edit !== 'new' && (edit as UserRow)?.id !== me.id && <Form.Item name="is_active" label="Активен" valuePropName="checked"><Switch /></Form.Item>}
        </Form>
      </Modal>
    </>
  );
}

function AuditLog() {
  const { data, isLoading } = useQuery({ queryKey: ['audit'], queryFn: () => api<AuditRow[]>('/audit?limit=500') });
  return (
    <Table<AuditRow> size="small" rowKey="id" loading={isLoading} dataSource={data} pagination={{ pageSize: 50 }} scroll={{ x: 800 }}
      columns={[
        { title: 'Когда', dataIndex: 'at', render: (v) => dateTime(v), width: 150 },
        ...(isSuper(useMe()) ? [{ title: 'Организация', dataIndex: 'organization', width: 140, render: (v: string | null) => v ?? '—' }] : []),
        { title: 'Кто', dataIndex: 'user_login', width: 140 },
        { title: 'Действие', dataIndex: 'action', width: 160 },
        { title: 'Объект', dataIndex: 'entity', width: 200 },
        { title: 'Подробности', dataIndex: 'details', render: (v) => (v ? <code style={{ fontSize: 12 }}>{JSON.stringify(v)}</code> : null) },
      ]} />
  );
}

function Profile() {
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const submit = async (v: { current_password: string; new_password: string }) => {
    try {
      await api('/auth/password', { method: 'POST', body: v });
      message.success('Пароль изменён');
      form.resetFields();
    } catch (e) {
      message.error((e as Error).message);
    }
  };
  return (
    <Card size="small" style={{ maxWidth: 420 }}>
      <Form form={form} layout="vertical" onFinish={submit}>
        <Form.Item name="current_password" label="Текущий пароль" rules={[{ required: true }]}><Input.Password autoComplete="current-password" /></Form.Item>
        <Form.Item name="new_password" label="Новый пароль" rules={[{ required: true }, { min: MIN_PASSWORD, message: `Не короче ${MIN_PASSWORD} символов` }]}>
          <Input.Password autoComplete="new-password" />
        </Form.Item>
        <Form.Item name="repeat" label="Ещё раз" dependencies={['new_password']} rules={[{ required: true }, ({ getFieldValue }) => ({
          validator: (_, v) => (v === getFieldValue('new_password') ? Promise.resolve() : Promise.reject(new Error('Пароли не совпадают'))),
        })]}>
          <Input.Password autoComplete="new-password" />
        </Form.Item>
        <Button type="primary" htmlType="submit">Сменить пароль</Button>
      </Form>
    </Card>
  );
}

export default function SettingsPage() {
  const me = useMe();
  const navigate = useNavigate();
  const location = useLocation();
  const tab = location.pathname.split('/')[2] ?? (isSuper(me) ? 'organizations' : isOwner(me) ? 'users' : 'profile');
  const unmapped = useMeta().data?.unmapped_fuels ?? 0;
  const items = [
    ...(isSuper(me) ? [{ key: 'organizations', label: 'Организации', children: <Organizations /> }] : []),
    ...(isOwner(me) ? [{ key: 'users', label: 'Пользователи', children: <Users /> }] : []),
    ...(isOwner(me) ? [{ key: 'fuels', label: <>Виды топлива {unmapped > 0 && <Badge count={unmapped} color="orange" size="small" />}</>, children: <FuelCatalog /> }] : []),
    ...(isOwner(me) ? [{ key: 'organization', label: 'Маржа и НДС', children: <OrganizationSettings /> }] : []),
    ...(isOwner(me) ? [{ key: 'payment-modes', label: 'Виды оплаты', children: <PaymentModes /> }] : []),
    ...(isOwner(me) ? [{ key: 'audit', label: 'Журнал действий', children: <AuditLog /> }] : []),
    { key: 'profile', label: 'Мой пароль', children: <Profile /> },
  ];
  return (
    <>
      <PageTitle title="Настройки" />
      <Tabs activeKey={tab} onChange={(k) => navigate(`/settings/${k}`)} items={items} destroyInactiveTabPane />
    </>
  );
}
