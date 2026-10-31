import { expect, Page, test } from '@playwright/test';

const login = process.env.E2E_LOGIN ?? 'admin';
const password = process.env.E2E_PASSWORD ?? 'demo-password-1';
const period = process.env.E2E_PERIOD ?? '';

export async function signIn(page: Page, user = login, pass = password) {
  await page.goto('/login');
  await page.getByLabel('Логин').fill(user);
  await page.getByLabel('Пароль').fill(pass);
  await page.getByRole('button', { name: 'Войти' }).click();
  await expect(page).not.toHaveURL(/login/);
}

/** Every page opens without JS errors or failed API calls; a screenshot is kept for review. */
const PAGES: { path: string; title: string; wait?: string }[] = [
  { path: '/', title: 'Обзор', wait: 'Выручка и литры' },
  { path: '/fuel', title: 'Топливо', wait: 'Цена по дням' },
  { path: '/demand', title: 'Спрос', wait: 'Тренд и прогноз' },
  { path: '/items', title: 'Товары', wait: 'Выручка по группам' },
  { path: '/finance', title: 'Финансы', wait: 'Выручка и маржа' },
  { path: '/plans', title: 'План-факт', wait: 'Нарастающим итогом' },
  { path: '/costs', title: 'Себестоимость', wait: 'Текущие цены' },
  { path: '/shifts', title: 'Смены' },
  { path: '/stations', title: 'АЗС' },
  { path: '/data', title: 'Данные' },
  { path: '/settings/users', title: 'Настройки' },
];

test('login rejects a wrong password', async ({ page }) => {
  await page.goto('/login');
  await page.getByLabel('Логин').fill(login);
  await page.getByLabel('Пароль').fill('wrong-password!');
  await page.getByRole('button', { name: 'Войти' }).click();
  await expect(page.getByText('Неверный логин или пароль')).toBeVisible();
});

for (const p of PAGES) {
  test(`page ${p.path} renders`, async ({ page }, info) => {
    const errors: string[] = [];
    page.on('pageerror', (e) => errors.push(e.message));
    page.on('response', (r) => { if (r.url().includes('/api/') && r.status() >= 400) errors.push(`${r.status()} ${r.url()}`); });
    await signIn(page);
    await page.goto(p.path + (p.path.startsWith('/settings') ? '' : period));
    await expect(page.getByRole('heading', { name: p.title, level: 3 })).toBeVisible();
    if (p.wait) await expect(page.getByText(p.wait).first()).toBeVisible();
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1500); // let chart animations finish before the screenshot
    await page.screenshot({ path: `test-results/screens/${info.project.name}${p.path.replace(/\//g, '_') || '_'}.png`, fullPage: true });
    expect(errors).toEqual([]);
  });
}

test('shift card opens from the list', async ({ page }, info) => {
  await signIn(page);
  await page.goto('/shifts' + period);
  await page.locator('.ant-table-row').first().click();
  await expect(page.getByText(/Смена №\d+/)).toBeVisible();
  await page.waitForLoadState('networkidle');
  await page.screenshot({ path: `test-results/screens/${info.project.name}_shift_card.png`, fullPage: true });
});

test('a viewer sees no money: no finance/costs menu, no margin', async ({ page, request }, info) => {
  test.skip(info.project.name !== 'desktop', 'one run is enough');
  // Owner creates (or reuses) a viewer through the API.
  const ownerLogin = await request.post('/api/v1/ui/auth/login', { headers: { 'X-Topaz-Ui': '1' }, data: { login, password } });
  expect(ownerLogin.ok()).toBeTruthy();
  const meta = await (await request.get('/api/v1/ui/meta')).json();
  await request.post('/api/v1/ui/users', {
    headers: { 'X-Topaz-Ui': '1' },
    data: { login: 'e2e-viewer', name: 'Наблюдатель', role: 'viewer', password: 'viewer-password-1', stations: meta.stations.map((s: any) => s.id) },
  });
  await signIn(page, 'e2e-viewer', 'viewer-password-1');
  await page.goto('/' + period);
  await expect(page.getByText('Выручка и литры').first()).toBeVisible();
  await expect(page.getByRole('menuitem', { name: 'Финансы' })).toHaveCount(0);
  await expect(page.getByRole('menuitem', { name: 'Себестоимость' })).toHaveCount(0);
  await expect(page.getByText('Валовая маржа')).toHaveCount(0);
});
