import { useQueryClient } from '@tanstack/react-query';
import { Alert, App, Button, Card, Flex, Form, Input, QRCode, Space, Steps, Typography } from 'antd';
import { useState } from 'react';
import { api } from '../api/client';

/** Turning two-factor login on: password → QR code for the app → first code → recovery codes (shown once). */
export function TotpSetup({ onDone }: { onDone?: () => void }) {
  const { message } = App.useApp();
  const qc = useQueryClient();
  const [setup, setSetup] = useState<{ secret: string; uri: string } | null>(null);
  const [codes, setCodes] = useState<string[] | null>(null);
  const [loading, setLoading] = useState(false);

  const start = async (v: { password: string }) => {
    setLoading(true);
    try {
      setSetup(await api<{ secret: string; uri: string }>('/auth/totp/setup', { method: 'POST', body: v }));
    } catch (e) {
      message.error((e as Error).message);
    } finally {
      setLoading(false);
    }
  };
  const confirm = async (v: { code: string }) => {
    setLoading(true);
    try {
      const r = await api<{ recovery_codes: string[] }>('/auth/totp/enable', { method: 'POST', body: v });
      setCodes(r.recovery_codes);
    } catch (e) {
      message.error((e as Error).message);
    } finally {
      setLoading(false);
    }
  };
  const finish = async () => {
    await qc.invalidateQueries();
    onDone?.();
  };

  const step = codes ? 2 : setup ? 1 : 0;
  return (
    <>
      <Steps size="small" current={step} style={{ marginBottom: 16 }}
        items={[{ title: 'Пароль' }, { title: 'Приложение' }, { title: 'Запасные коды' }]} />
      {step === 0 && (
        <Form layout="vertical" onFinish={start}>
          <Typography.Paragraph>
            При входе кроме пароля нужно будет ввести шестизначный код из приложения на телефоне: Яндекс Ключ,
            Google Authenticator или любого похожего. Даже если пароль узнают, без телефона войти не получится.
          </Typography.Paragraph>
          <Form.Item name="password" label="Ваш пароль" rules={[{ required: true, message: 'Введите пароль' }]}>
            <Input.Password autoComplete="current-password" />
          </Form.Item>
          <Button type="primary" htmlType="submit" loading={loading}>Дальше</Button>
        </Form>
      )}
      {step === 1 && setup && (
        <Form layout="vertical" onFinish={confirm}>
          <Typography.Paragraph>
            Откройте приложение, нажмите «Добавить» и наведите камеру на QR-код. Если камера недоступна, введите ключ вручную.
          </Typography.Paragraph>
          <Flex gap={16} wrap align="center" style={{ marginBottom: 16 }}>
            <QRCode value={setup.uri} size={180} bordered={false} style={{ background: '#fff' }} />
            <div>
              <Typography.Text type="secondary">Ключ:</Typography.Text>
              <br />
              <Typography.Text code copyable style={{ wordBreak: 'break-all' }}>{setup.secret}</Typography.Text>
            </div>
          </Flex>
          <Form.Item name="code" label="Код из приложения" rules={[{ required: true, pattern: /^\d{6}$/, message: '6 цифр' }]}>
            <Input inputMode="numeric" autoComplete="one-time-code" maxLength={6} style={{ maxWidth: 160 }} />
          </Form.Item>
          <Button type="primary" htmlType="submit" loading={loading}>Включить</Button>
        </Form>
      )}
      {step === 2 && codes && (
        <>
          <Alert type="success" showIcon message="Двухфакторная защита включена" style={{ marginBottom: 12 }} />
          <Typography.Paragraph>
            Сохраните запасные коды: распечатайте или запишите и уберите в надёжное место. Если телефон потеряется,
            каждый код подойдёт вместо кода из приложения один раз. Больше они не покажутся.
          </Typography.Paragraph>
          <Card size="small" style={{ maxWidth: 320, marginBottom: 12 }}>
            <Typography.Paragraph copyable={{ text: codes.join('\n') }} style={{ fontFamily: 'monospace', marginBottom: 0 }}>
              {codes.map((c) => <div key={c}>{c}</div>)}
            </Typography.Paragraph>
          </Card>
          <Space>
            <Button type="primary" onClick={finish}>Я сохранил коды</Button>
          </Space>
        </>
      )}
    </>
  );
}

/** Two-factor login status and switches for «Профиль». */
export function TotpStatus({ enabled, required, codesLeft }: { enabled: boolean; required: boolean; codesLeft: number }) {
  const { message } = App.useApp();
  const qc = useQueryClient();
  const [settingUp, setSettingUp] = useState(false);
  const [disabling, setDisabling] = useState(false);

  const disable = async (v: { password: string; code: string }) => {
    try {
      await api('/auth/totp/disable', { method: 'POST', body: v });
      message.success('Двухфакторная защита выключена');
      setDisabling(false);
      qc.invalidateQueries({ queryKey: ['me'] });
    } catch (e) {
      message.error((e as Error).message);
    }
  };

  if (settingUp) return <TotpSetup onDone={() => setSettingUp(false)} />;
  if (!enabled)
    return (
      <>
        <Typography.Paragraph>Двухфакторная защита выключена: для входа достаточно пароля.</Typography.Paragraph>
        <Button type="primary" onClick={() => setSettingUp(true)}>Включить двухфакторную защиту</Button>
      </>
    );
  return (
    <>
      <Alert type="success" showIcon style={{ marginBottom: 12 }}
        message="Двухфакторная защита включена"
        description={`При входе нужен код из приложения. Осталось запасных кодов: ${codesLeft}.`} />
      {codesLeft <= 2 && (
        <Alert type="warning" showIcon style={{ marginBottom: 12 }}
          message="Запасные коды заканчиваются"
          description="Попросите владельца или суперадминистратора сбросить защиту и включите её заново: появятся новые коды." />
      )}
      {required ? (
        <Typography.Text type="secondary">Для суперадминистратора защита обязательна и не выключается.</Typography.Text>
      ) : disabling ? (
        <Form layout="vertical" onFinish={disable} style={{ maxWidth: 320 }}>
          <Form.Item name="password" label="Пароль" rules={[{ required: true }]}><Input.Password autoComplete="current-password" /></Form.Item>
          <Form.Item name="code" label="Код из приложения или запасной код" rules={[{ required: true }]}>
            <Input autoComplete="one-time-code" />
          </Form.Item>
          <Space>
            <Button danger htmlType="submit">Выключить</Button>
            <Button onClick={() => setDisabling(false)}>Отмена</Button>
          </Space>
        </Form>
      ) : (
        <Button onClick={() => setDisabling(true)}>Выключить двухфакторную защиту</Button>
      )}
    </>
  );
}
