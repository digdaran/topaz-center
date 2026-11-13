import { App, Button, Card, Flex, Form, Input } from 'antd';
import { api } from '../api/client';
import { useMe } from '../auth/auth';
import { Passkeys } from '../components/Passkeys';
import { TotpStatus } from '../components/TotpSetup';
import { PageTitle } from '../components/ui';

const MIN_PASSWORD = 10;

/** «Профиль» (user menu, top right): the user's own password and second factors. */
export default function ProfilePage() {
  const me = useMe();
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
    <>
    <PageTitle title="Профиль" description={`${me.name} · вход в Topaz Center: пароль, код из приложения, отпечаток или лицо`} />
    <Flex gap={16} wrap align="start">
    <Card size="small" title="Пароль" style={{ width: 420, maxWidth: '100%' }}>
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
    <Card size="small" title="Двухфакторная защита" style={{ width: 560, maxWidth: '100%' }}>
      <TotpStatus enabled={me.totp_enabled} required={me.totp_mandatory} codesLeft={me.recovery_codes_left} />
    </Card>
    <Card size="small" title="Вход по отпечатку или лицу" style={{ width: 560, maxWidth: '100%' }}>
      <Passkeys />
    </Card>
    </Flex>
    </>
  );
}
