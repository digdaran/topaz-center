import { BarChartOutlined, KeyOutlined, LockOutlined, SafetyOutlined, UserOutlined } from '@ant-design/icons';
import { useQueryClient } from '@tanstack/react-query';
import { Alert, Button, Card, Divider, Flex, Form, Input, Typography } from 'antd';
import { useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import { VersionLabel } from '../components/appUpdate';
import { safeNext } from '../lib/safeNext';
import { getPasskey, passkeyError, passkeysSupported, RequestOptionsJson } from '../lib/webauthn';

export default function LoginPage() {
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  // Two-factor login: after the password, a code from the app (or a recovery code).
  const [needCode, setNeedCode] = useState(false);
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const qc = useQueryClient();

  const done = async () => {
    await qc.invalidateQueries({ queryKey: ['me'] });
    const next = params.get('next');
    navigate(safeNext(next), { replace: true });
  };

  const submit = async (v: { login: string; password: string }) => {
    setLoading(true);
    setError(null);
    try {
      const r = await api<{ need_code?: boolean }>('/auth/login', { method: 'POST', body: v });
      if (r.need_code) setNeedCode(true);
      else await done();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoading(false);
    }
  };

  // Passkey: no login or password — the device offers its key for this site after the fingerprint or face.
  const passkey = async () => {
    setLoading(true);
    setError(null);
    try {
      const options = await api<RequestOptionsJson>('/auth/passkey/options', { method: 'POST', body: {} });
      const credential = await getPasskey(options);
      await api('/auth/passkey/login', { method: 'POST', body: credential });
      await done();
    } catch (e) {
      setError(passkeyError(e));
    } finally {
      setLoading(false);
    }
  };

  const submitCode = async (v: { code: string }) => {
    setLoading(true);
    setError(null);
    try {
      await api('/auth/login/code', { method: 'POST', body: v });
      await done();
    } catch (e) {
      setError((e as Error).message);
      // The 5-minute window is over: start again from the password.
      if ((e as Error).message.includes('войдите заново')) setNeedCode(false);
    } finally {
      setLoading(false);
    }
  };

  return (
    <Flex align="center" justify="center" style={{ minHeight: '100vh', padding: 16 }}>
      <div style={{ position: 'fixed', left: 16, bottom: 12 }}><VersionLabel linked={false} /></div>
      <Card style={{ width: 380, maxWidth: '100%' }}>
        <Flex vertical align="center" gap={4} style={{ marginBottom: 24 }}>
          <BarChartOutlined style={{ fontSize: 36, color: '#1668dc' }} />
          <Typography.Title level={3} style={{ margin: 0 }}>Topaz Center</Typography.Title>
          <Typography.Text type="secondary">Данные и аналитика АЗС</Typography.Text>
        </Flex>
        {error && <Alert type="error" showIcon message={error} style={{ marginBottom: 16 }} />}
        {needCode ? (
          <Form layout="vertical" onFinish={submitCode} requiredMark={false}>
            <Form.Item name="code" label="Код из приложения" extra="Шесть цифр из Яндекс Ключа или Google Authenticator. Без телефона введите один из запасных кодов."
              rules={[{ required: true, message: 'Введите код' }]}>
              <Input prefix={<SafetyOutlined />} inputMode="numeric" autoComplete="one-time-code" autoFocus size="large" />
            </Form.Item>
            <Button type="primary" htmlType="submit" block size="large" loading={loading}>Войти</Button>
            <Button type="link" block onClick={() => { setNeedCode(false); setError(null); }}>Назад</Button>
          </Form>
        ) : (
        <Form layout="vertical" onFinish={submit} requiredMark={false}>
          <Form.Item name="login" label="Логин" rules={[{ required: true, message: 'Введите логин' }]}>
            <Input prefix={<UserOutlined />} autoComplete="username" autoFocus size="large" />
          </Form.Item>
          <Form.Item name="password" label="Пароль" rules={[{ required: true, message: 'Введите пароль' }]}>
            <Input.Password prefix={<LockOutlined />} autoComplete="current-password" size="large" />
          </Form.Item>
          <Button type="primary" htmlType="submit" block size="large" loading={loading}>Войти</Button>
          {passkeysSupported() && (
            <>
              <Divider plain style={{ margin: '16px 0' }}>или</Divider>
              <Button block size="large" icon={<KeyOutlined />} onClick={passkey} loading={loading}>Войти по отпечатку или лицу</Button>
              <Typography.Paragraph type="secondary" style={{ fontSize: 12, marginTop: 8, marginBottom: 0, textAlign: 'center' }}>
                Сначала добавьте устройство: Настройки → Вход и защита
              </Typography.Paragraph>
            </>
          )}
        </Form>
        )}
      </Card>
    </Flex>
  );
}
