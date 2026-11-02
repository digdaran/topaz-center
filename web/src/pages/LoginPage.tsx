import { BarChartOutlined, LockOutlined, UserOutlined } from '@ant-design/icons';
import { useQueryClient } from '@tanstack/react-query';
import { Alert, Button, Card, Flex, Form, Input, Typography } from 'antd';
import { useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import { VersionLabel } from '../components/appUpdate';

export default function LoginPage() {
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const qc = useQueryClient();

  const submit = async (v: { login: string; password: string }) => {
    setLoading(true);
    setError(null);
    try {
      await api('/auth/login', { method: 'POST', body: v });
      await qc.invalidateQueries({ queryKey: ['me'] });
      const next = params.get('next');
      navigate(next && next.startsWith('/') && !next.startsWith('//') ? next : '/', { replace: true });
    } catch (e) {
      setError((e as Error).message);
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
        <Form layout="vertical" onFinish={submit} requiredMark={false}>
          <Form.Item name="login" label="Логин" rules={[{ required: true, message: 'Введите логин' }]}>
            <Input prefix={<UserOutlined />} autoComplete="username" autoFocus size="large" />
          </Form.Item>
          <Form.Item name="password" label="Пароль" rules={[{ required: true, message: 'Введите пароль' }]}>
            <Input.Password prefix={<LockOutlined />} autoComplete="current-password" size="large" />
          </Form.Item>
          <Button type="primary" htmlType="submit" block size="large" loading={loading}>Войти</Button>
        </Form>
      </Card>
    </Flex>
  );
}
