import { Card, Flex } from 'antd';
import { useMe } from '../auth/auth';
import { PasswordForm } from '../components/PasswordForm';
import { Passkeys } from '../components/Passkeys';
import { TotpStatus } from '../components/TotpSetup';
import { PageTitle } from '../components/ui';

/** «Профиль» (user menu, top right): the user's own password and second factors. */
export default function ProfilePage() {
  const me = useMe();
  return (
    <>
    <PageTitle title="Профиль" description={`${me.name} · вход в АЗС Хаб: пароль, код из приложения, отпечаток или лицо`} />
    <Flex gap={16} wrap align="start">
    <Card size="small" title="Пароль" style={{ width: 420, maxWidth: '100%' }}>
      <PasswordForm />
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
