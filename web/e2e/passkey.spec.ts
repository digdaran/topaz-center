import { expect, test } from '@playwright/test';

const login = process.env.E2E_LOGIN ?? 'admin';
const password = process.env.E2E_PASSWORD ?? 'demo-password-1';

// A real browser makes and uses a passkey: Chrome's virtual authenticator stands in for the fingerprint sensor.
// Needs a secure context: run against http://localhost (or https).
test('a passkey added in the settings logs in without a password', async ({ page, context, browserName }) => {
  test.skip(browserName !== 'chromium', 'virtual authenticator is a Chrome DevTools feature');
  const cdp = await context.newCDPSession(page);
  await cdp.send('WebAuthn.enable');
  await cdp.send('WebAuthn.addVirtualAuthenticator', {
    options: { protocol: 'ctap2', transport: 'internal', hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true },
  });

  await page.goto('/login');
  await page.getByLabel('Логин').fill(login);
  await page.getByLabel('Пароль').fill(password);
  await page.getByRole('button', { name: 'Войти', exact: true }).click();
  await expect(page).not.toHaveURL(/login/);
  await page.goto('/settings/profile');
  await page.getByRole('button', { name: 'Добавить это устройство' }).click();
  await page.getByLabel('Ваш пароль').fill(password);
  await page.getByLabel('Название устройства').fill('E2E ключ');
  await page.getByRole('button', { name: 'Добавить', exact: true }).click();
  await expect(page.getByText('Готово: теперь на этом устройстве можно входить без пароля')).toBeVisible();
  await expect(page.getByText('E2E ключ')).toBeVisible();

  await context.clearCookies();
  await page.goto('/login');
  await page.getByRole('button', { name: 'Войти по отпечатку или лицу' }).click();
  await expect(page).not.toHaveURL(/login/);

  // Clean up: remove the passkey again.
  await page.goto('/settings/profile');
  await page.getByRole('button', { name: 'Удалить' }).first().click();
  await page.getByRole('button', { name: 'Удалить', exact: true }).last().click();
  await expect(page.getByText('Ключ удалён')).toBeVisible();
});
