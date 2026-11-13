import { PlusOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { App, Button, Flex, Form, Input, Modal, Select, Switch, Table, Tag, Typography } from 'antd';
import { useState } from 'react';
import { api, isSuper, useMeta } from '../api/client';
import { useMe } from '../auth/auth';
import { Section } from '../components/Section';
import { section } from '../nav';
import { FuelCatalog } from './FuelCatalog';
import { OrganizationSettings, PaymentModes } from './OrgSettings';
import { dateTime } from '../lib/format';
import { AUDIT_WARN, auditAction, auditDetails, auditEntity } from '../lib/audit';

interface UserRow { id: string; login: string; name: string; role: string; is_active: boolean; totp_enabled: boolean; passkeys: number; created_at: string; last_login_at: string | null; locked_until: string | null; stations: string[]; organization_id: string | null; organization: string | null }
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
      else await api(`/users/${(edit as UserRow).id}`, { method: 'PATCH', body: { name: v.name, role: v.role, is_active: v.is_active, stations: stationsSelectable ? v.stations : undefined, password: v.password || null, organization_id: sup ? v.organization_id : undefined, reset_totp: v.reset_totp || undefined } });
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
          { title: 'Код при входе', dataIndex: 'totp_enabled', render: (v: boolean) => (v ? <Tag color="green">включён</Tag> : <Tag>нет</Tag>) },
          { title: 'Вход по отпечатку', dataIndex: 'passkeys', render: (v: number) => (v ? <Tag color="green">{v} устр.</Tag> : <Tag>нет</Tag>) },
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
          {edit !== 'new' && ((edit as UserRow)?.totp_enabled || (edit as UserRow)?.passkeys > 0) && (edit as UserRow)?.id !== me.id && (
            <Form.Item name="reset_totp" valuePropName="checked" label="Сбросить код при входе и вход по отпечатку"
              extra="Если пользователь потерял телефон: код из приложения, запасные коды и вход по отпечатку или лицу перестанут работать. Он войдёт по паролю и настроит защиту заново.">
              <Switch />
            </Form.Item>
          )}
        </Form>
      </Modal>
    </>
  );
}

function AuditLog() {
  const me = useMe();
  const meta = useMeta();
  const { data, isLoading } = useQuery({ queryKey: ['audit'], queryFn: () => api<AuditRow[]>('/audit?limit=500') });
  const users = useQuery({ queryKey: ['users'], queryFn: () => api<UserRow[]>('/users') });
  const [logins, setLogins] = useState(false);
  const [who, setWho] = useState<string | undefined>();
  // Names for "station:<id>", "user:<id>", "organization:<id>" and ids inside the details.
  const names: Record<string, string> = Object.fromEntries([
    ...(meta.data?.stations ?? []).map((st) => [st.id, st.name]),
    ...(users.data ?? []).map((u) => [u.id, u.name || u.login]),
    ...(me.organizations ?? []).map((o) => [o.id, o.name]),
  ]);
  const rows = (data ?? []).filter((r) => (logins || r.action !== 'login') && (!who || r.user_login === who));
  const people = [...new Set((data ?? []).map((r) => r.user_login).filter(Boolean))] as string[];
  return (
    <>
      <Flex wrap gap={12} align="center" style={{ marginBottom: 12 }}>
        <Select allowClear placeholder="Все сотрудники" style={{ width: 220 }} value={who} onChange={setWho} options={people.map((p) => ({ value: p, label: p }))} />
        <span><Switch size="small" checked={logins} onChange={setLogins} /> показывать обычные входы в сервис</span>
        <Typography.Text type="secondary">Неудачные попытки входа видны всегда.</Typography.Text>
      </Flex>
      <Table<AuditRow> size="small" rowKey="id" loading={isLoading} dataSource={rows} pagination={{ pageSize: 50 }} scroll={{ x: 'max-content' }}
        columns={[
          { title: 'Когда', dataIndex: 'at', render: (v) => <span style={{ whiteSpace: 'nowrap' }}>{dateTime(v)}</span> },
          ...(isSuper(me) ? [{ title: 'Организация', dataIndex: 'organization', render: (v: string | null) => v ?? '—' }] : []),
          { title: 'Кто', dataIndex: 'user_login', render: (v: string | null) => v ?? '—' },
          {
            title: 'Что сделал', dataIndex: 'action',
            render: (v: string, r) => (
              <>
                <Typography.Text type={AUDIT_WARN.has(v) ? 'danger' : undefined}>{auditAction(v)}</Typography.Text>
                {r.entity && <div style={{ fontSize: 12, opacity: 0.7 }}>{auditEntity(r.entity, names)}</div>}
              </>
            ),
          },
          {
            title: 'Подробности', dataIndex: 'details',
            render: (v) => { const t = auditDetails(v, names); return t ? <span style={{ fontSize: 13 }}>{t}</span> : null; },
          },
        ]} />
    </>
  );
}

export default function SettingsPage() {
  const unmapped = useMeta().data?.unmapped_fuels ?? 0;
  return (
    <Section section={section('/settings')} badges={{ fuels: unmapped }} panes={{
      users: () => <Users />,
      'payment-modes': () => <PaymentModes />,
      fuels: () => <FuelCatalog />,
      organization: () => <OrganizationSettings />,
      audit: () => <AuditLog />,
    }} />
  );
}
