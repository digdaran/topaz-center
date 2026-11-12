import { DeleteOutlined, KeyOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, App, Button, Form, Input, List, Popconfirm, Space, Typography } from 'antd';
import { useState } from 'react';
import { api } from '../api/client';
import { dateTime } from '../lib/format';
import { createPasskey, CreationOptionsJson, passkeyError, passkeysSupported } from '../lib/webauthn';

interface Passkey { id: string; name: string; created_at: string; last_used_at: string | null }

/** «Настройки → Вход и защита»: passkeys of the user — add this device, see where they are, remove a lost one. */
export function Passkeys() {
  const { message } = App.useApp();
  const qc = useQueryClient();
  const { data, isLoading } = useQuery({ queryKey: ['passkeys'], queryFn: () => api<Passkey[]>('/auth/passkeys') });
  const [adding, setAdding] = useState(false);
  const [loading, setLoading] = useState(false);
  const supported = passkeysSupported();

  const add = async (v: { password: string; name?: string }) => {
    setLoading(true);
    try {
      const options = await api<CreationOptionsJson>('/auth/passkeys/options', { method: 'POST', body: { password: v.password } });
      const credential = await createPasskey(options);
      await api('/auth/passkeys', { method: 'POST', body: { credential, name: v.name || null } });
      message.success('Готово: теперь на этом устройстве можно входить без пароля');
      setAdding(false);
      qc.invalidateQueries({ queryKey: ['passkeys'] });
    } catch (e) {
      message.error(passkeyError(e));
    } finally {
      setLoading(false);
    }
  };

  const remove = async (id: string) => {
    try {
      await api(`/auth/passkeys/${id}`, { method: 'DELETE' });
      message.success('Ключ удалён, вход с того устройства закрыт');
      qc.invalidateQueries({ queryKey: ['passkeys'] });
    } catch (e) {
      message.error((e as Error).message);
    }
  };

  return (
    <>
      <Typography.Paragraph>
        Входите без пароля и кода: по отпечатку пальца, лицу (Face ID) или PIN-коду телефона или компьютера (Windows Hello).
        Это так же надёжно, как пароль с кодом: войти можно только с вашего устройства и только после вашего отпечатка или лица.
        Добавьте каждое устройство, с которого заходите.
      </Typography.Paragraph>
      <List<Passkey>
        size="small" loading={isLoading} dataSource={data ?? []} locale={{ emptyText: 'Устройств пока нет' }} style={{ marginBottom: 12 }}
        renderItem={(p) => (
          <List.Item actions={[
            <Popconfirm key="del" title="Удалить ключ?" description="С этого устройства нужно будет входить по паролю. Другие сеансы завершатся."
              okText="Удалить" cancelText="Отмена" onConfirm={() => remove(p.id)}>
              <Button size="small" danger icon={<DeleteOutlined />}>Удалить</Button>
            </Popconfirm>,
          ]}>
            <List.Item.Meta avatar={<KeyOutlined />} title={p.name}
              description={`добавлен ${dateTime(p.created_at)}${p.last_used_at ? `, последний вход ${dateTime(p.last_used_at)}` : ''}`} />
          </List.Item>
        )}
      />
      {!supported ? (
        <Alert type="info" showIcon message="Этот браузер не поддерживает вход по отпечатку или лицу. Откройте сервис в Chrome, Safari, Edge или Яндекс Браузере свежей версии." />
      ) : adding ? (
        <Form layout="vertical" onFinish={add} style={{ maxWidth: 320 }}>
          <Form.Item name="password" label="Ваш пароль" rules={[{ required: true, message: 'Введите пароль' }]}>
            <Input.Password autoComplete="current-password" />
          </Form.Item>
          <Form.Item name="name" label="Название устройства" extra="Например, «Мой iPhone» или «Рабочий ноутбук». Можно не заполнять.">
            <Input maxLength={60} />
          </Form.Item>
          <Space>
            <Button type="primary" htmlType="submit" loading={loading}>Добавить</Button>
            <Button onClick={() => setAdding(false)}>Отмена</Button>
          </Space>
        </Form>
      ) : (
        <Button type="primary" icon={<KeyOutlined />} onClick={() => setAdding(true)}>Добавить это устройство</Button>
      )}
    </>
  );
}
