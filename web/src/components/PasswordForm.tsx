import { App, Button, Form, Input } from 'antd';
import { api } from '../api/client';

const MIN_PASSWORD = 10;

/** Change the own password (current + new twice); used in «Профиль» and on the first sign-in with a temporary one. */
export function PasswordForm({ onChanged, currentLabel = 'Текущий пароль' }: { onChanged?: () => void; currentLabel?: string }) {
  const { message } = App.useApp();
  const [form] = Form.useForm();
  const submit = async (v: { current_password: string; new_password: string }) => {
    try {
      await api('/auth/password', { method: 'POST', body: { current_password: v.current_password, new_password: v.new_password } });
      message.success('Пароль изменён');
      form.resetFields();
      onChanged?.();
    } catch (e) {
      message.error((e as Error).message);
    }
  };
  return (
    <Form form={form} layout="vertical" onFinish={submit}>
      <Form.Item name="current_password" label={currentLabel} rules={[{ required: true }]}><Input.Password autoComplete="current-password" /></Form.Item>
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
  );
}
