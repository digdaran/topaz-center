import { SendOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, App, Button, Flex, Form, Popconfirm, Select, Space, Switch, Typography } from 'antd';
import { useEffect, useState } from 'react';
import { api } from '../api/client';
import { severityOptions } from '../lib/severity';

interface Channel { title: string; min_severity: string; daily_digest: boolean; enabled: boolean }
interface Me { bot_configured: boolean; bot_username: string | null; channel: Channel | null }
interface Link { url: string; expires_at: string }

/**
 * Opens a t.me link in a new tab (Telegram app on phones). Browsers may block a tab opened after a server call,
 * so the caller also shows the link as a button.
 */
export function openTelegram(url: string) {
  window.open(url, '_blank', 'noopener');
}

/** «Настройки → Уведомления в Telegram»: the user's own chat with the bot, linked by a one-time link. */
export function TelegramPersonal() {
  const { message } = App.useApp();
  const qc = useQueryClient();
  const [link, setLink] = useState<Link | null>(null);
  const [busy, setBusy] = useState(false);
  const waiting = !!link && new Date(link.expires_at) > new Date();
  const { data } = useQuery({
    queryKey: ['telegram-me'],
    queryFn: () => api<Me>('/telegram/me'),
    // While the user is in Telegram, check every few seconds whether the bot got the link.
    refetchInterval: (q) => (waiting && !q.state.data?.channel ? 3000 : false),
  });
  const channel = data?.channel ?? null;

  useEffect(() => {
    if (channel && link) {
      setLink(null);
      message.success('Telegram подключён');
    }
  }, [channel, link, message]);

  const connect = async (v: { min_severity: string; daily_digest: boolean }) => {
    setBusy(true);
    try {
      const l = await api<Link>('/telegram/me/link', { method: 'POST', body: v });
      setLink(l);
      openTelegram(l.url);
    } catch (e) {
      message.error((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const save = async (patch: Partial<Channel>) => {
    if (!channel) return;
    try {
      await api('/telegram/me', { method: 'PUT', body: { ...channel, ...patch } });
      qc.invalidateQueries({ queryKey: ['telegram-me'] });
    } catch (e) {
      message.error((e as Error).message);
    }
  };

  const run = async (action: () => Promise<unknown>, ok: string) => {
    try {
      await action();
      message.success(ok);
      qc.invalidateQueries({ queryKey: ['telegram-me'] });
    } catch (e) {
      message.error((e as Error).message);
    }
  };

  if (!data) return null;
  if (!data.bot_configured)
    return <Alert type="info" showIcon message="Администратор ещё не подключил бота Telegram к сервису." />;

  if (channel)
    return (
      <>
        <Typography.Paragraph>
          Уведомления приходят вам в Telegram от бота{data.bot_username && <> <b>@{data.bot_username}</b></>} — только по вашим АЗС.
        </Typography.Paragraph>
        {!channel.enabled && (
          <Alert type="warning" showIcon style={{ marginBottom: 12 }}
            message="Уведомления выключены"
            description="Если вы заблокировали бота в Telegram, разблокируйте его и включите уведомления снова. Не помогает — нажмите «Отключить» и подключите заново." />
        )}
        <Form layout="vertical" style={{ maxWidth: 360 }}>
          <Form.Item label="Присылать события с важностью от">
            <Select value={channel.min_severity} options={severityOptions} onChange={(v) => save({ min_severity: v })} />
          </Form.Item>
          <Flex gap={24}>
            <Form.Item label="Утренняя сводка в 9:00">
              <Switch checked={channel.daily_digest} onChange={(v) => save({ daily_digest: v })} />
            </Form.Item>
            <Form.Item label="Уведомления включены">
              <Switch checked={channel.enabled} onChange={(v) => save({ enabled: v })} />
            </Form.Item>
          </Flex>
        </Form>
        <Space>
          <Button icon={<SendOutlined />} onClick={() => run(() => api('/telegram/me/test', { method: 'POST' }), 'Тестовое сообщение отправлено')}>Тест</Button>
          <Popconfirm title="Отключить Telegram?" okText="Отключить" cancelText="Отмена"
            onConfirm={() => run(() => api('/telegram/me', { method: 'DELETE' }), 'Telegram отключён')}>
            <Button danger>Отключить</Button>
          </Popconfirm>
        </Space>
      </>
    );

  return (
    <>
      <Typography.Paragraph>
        События «Контроля» по вашим АЗС будут приходить вам в Telegram. Нажмите «Подключить Telegram», в открывшемся Telegram нажмите
        «Запустить» (или «Start») — и всё.
      </Typography.Paragraph>
      <Form layout="vertical" onFinish={connect} initialValues={{ min_severity: 'warning', daily_digest: true }} style={{ maxWidth: 360 }}>
        <Form.Item name="min_severity" label="Присылать события с важностью от">
          <Select options={severityOptions} />
        </Form.Item>
        <Form.Item name="daily_digest" label="Утренняя сводка в 9:00" valuePropName="checked">
          <Switch />
        </Form.Item>
        <Button type="primary" htmlType="submit" icon={<SendOutlined />} loading={busy}>Подключить Telegram</Button>
      </Form>
      {waiting && link && (
        <Alert type="info" showIcon style={{ marginTop: 12 }}
          message="Ждём подтверждения из Telegram…"
          description={<>
            В Telegram нажмите «Запустить». Если Telegram не открылся — <a href={link.url} target="_blank" rel="noopener noreferrer">откройте ссылку</a>.
            Ссылка одноразовая и действует 15 минут.
          </>} />
      )}
    </>
  );
}
