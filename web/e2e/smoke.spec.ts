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
  { path: '/tanks', title: 'Запасы топлива', wait: 'Приём топлива' },
  { path: '/control', title: 'Контроль' },
  { path: '/shifts', title: 'Смены' },
  { path: '/stations', title: 'АЗС' },
  { path: '/data', title: 'Данные' },
  { path: '/settings/organizations', title: 'Настройки', wait: 'Добавить организацию' },
  { path: '/settings/users', title: 'Настройки' },
  { path: '/settings/fuels', title: 'Настройки', wait: 'Подтвердить все предложения' },
  { path: '/settings/fuels?view=reference', title: 'Настройки', wait: 'Добавить вид' },
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
    await page.screenshot({ path: `test-results/screens/${info.project.name}${p.path.replace(/[^a-z0-9]+/gi, '_') || '_'}.png`, fullPage: true });
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

test('owner confirms suggested fuel types in one click (then the test undoes it)', async ({ page }, info) => {
  test.skip(info.project.name !== 'desktop', 'changes shared demo data; once is enough');
  await signIn(page);
  await page.goto('/settings/fuels');
  const button = page.getByRole('button', { name: /Подтвердить все предложения/ });
  await expect(button).toBeVisible();
  const startedAt = new Date().toISOString();
  if (await button.isEnabled()) {
    await button.click();
    await page.getByRole('button', { name: 'Принять' }).click();
  }
  await expect(page.getByText('Всё топливо сопоставлено со справочником')).toBeVisible();
  await expect(page.locator('.ant-menu').getByText('Настройки').locator('..').locator('.ant-badge-count')).toHaveCount(0);

  // Undo the confirmations this test made, so the demo keeps showing pending fuels.
  const headers = { 'X-Topaz-Ui': '1' };
  const all = await (await page.request.get('/api/v1/ui/catalog/fuels', { headers })).json();
  for (const f of all.fuels.filter((x: { confirmed: boolean; confirmed_at: string }) => x.confirmed && new Date(x.confirmed_at) >= new Date(startedAt)))
    expect((await page.request.put(`/api/v1/ui/catalog/fuels/${f.station_id}/${f.fuel_id}`, { headers, data: { fuel_type: null } })).ok()).toBeTruthy();
});

test('owner sees the agent update rollout', async ({ page }, info) => {
  await signIn(page);
  await page.goto('/stations');
  await page.getByRole('tab', { name: 'Обновление агентов' }).click();
  await expect(page.getByText('Раскатка')).toBeVisible();
  await expect(page.getByText('автоматическое обновление')).toBeVisible();
  await page.waitForLoadState('networkidle');
  await page.screenshot({ path: `test-results/screens/${info.project.name}_agent_updates.png`, fullPage: true });
});

test('station list shows the PC health column', async ({ page }, info) => {
  await signIn(page);
  await page.goto('/stations');
  await page.getByRole('tab', { name: 'Состояние и настройки' }).click();
  await expect(page.getByRole('columnheader', { name: 'ПК АЗС' })).toBeVisible();
  await page.waitForLoadState('networkidle');
  await page.screenshot({ path: `test-results/screens/${info.project.name}_stations_status.png`, fullPage: true });
});

test('superadmin sees connection requests', async ({ page }, info) => {
  await signIn(page);
  await page.goto('/stations?tab=connect');
  await expect(page.getByText('Решения за 30 дней')).toBeVisible();
  await page.waitForLoadState('networkidle');
  await page.screenshot({ path: `test-results/screens/${info.project.name}_station_requests.png`, fullPage: true });
});

test('an owner of another organization sees none of the stations; the superadmin switches between them', async ({ page, request }, info) => {
  test.skip(info.project.name !== 'desktop', 'creates an organization; once is enough');
  const headers = { 'X-Topaz-Ui': '1' };
  expect((await request.post('/api/v1/ui/auth/login', { headers, data: { login, password } })).ok()).toBeTruthy();
  const main = await (await request.get('/api/v1/ui/meta')).json();
  expect(main.stations.length).toBeGreaterThan(0);
  const orgs = await (await request.get('/api/v1/ui/organizations')).json();
  let other = orgs.find((o: { name: string }) => o.name === 'E2E Другой хозяин');
  if (!other) {
    const r = await request.post('/api/v1/ui/organizations', { headers, data: { name: 'E2E Другой хозяин' } });
    expect(r.ok()).toBeTruthy();
    other = await r.json();
  }
  await request.post('/api/v1/ui/users', {
    headers, data: { login: 'e2e-other-owner', name: 'Другой хозяин', role: 'owner', password: 'other-password-1', organization_id: other.id },
  });

  await signIn(page, 'e2e-other-owner', 'other-password-1');
  await page.goto('/stations');
  await expect(page.getByText('E2E Другой хозяин').first()).toBeVisible();
  for (const s of main.stations) await expect(page.getByText(s.name, { exact: true })).toHaveCount(0);
  const meta = await (await page.request.get('/api/v1/ui/meta')).json();
  expect(meta.stations).toEqual([]);

  // superadmin: switcher in the header
  await page.context().clearCookies();
  await signIn(page);
  await page.goto('/stations');
  await expect(page.getByText(main.stations[0].name).first()).toBeVisible();
  await page.screenshot({ path: `test-results/screens/${info.project.name}_org_switcher.png` });
  const back = await page.request.post('/api/v1/ui/session/organization', { headers, data: { id: other.id } });
  expect(back.ok()).toBeTruthy();
  expect((await (await page.request.get('/api/v1/ui/meta')).json()).stations).toEqual([]);
  const home = orgs.find((o: { stations: number }) => o.stations > 0) ?? orgs[0];

  // transfer a station from Settings → Organizations, then give it back
  const station = main.stations[0];
  await page.goto('/settings/organizations');
  await page.getByRole('button', { name: 'Передать АЗС другой организации' }).click();
  const dialog = page.getByRole('dialog', { name: 'Передать АЗС другой организации' });
  await dialog.getByLabel('АЗС').click();
  await page.locator('.ant-select-item-option').filter({ hasText: station.name }).click();
  await dialog.getByLabel('Новая организация').click();
  await page.locator('.ant-select-item-option').filter({ hasText: 'E2E Другой хозяин' }).click();
  await page.screenshot({ path: `test-results/screens/${info.project.name}_org_transfer.png` });
  await dialog.getByRole('button', { name: 'Передать' }).click();
  await expect(page.getByText('АЗС передана')).toBeVisible();
  const after = await (await page.request.get('/api/v1/ui/organizations')).json();
  expect(after.find((o: { id: string }) => o.id === other.id).station_list.map((s: { id: string }) => s.id)).toContain(station.id);
  expect((await page.request.post(`/api/v1/ui/stations/${station.id}/organization`, { headers, data: { organization_id: home.id } })).ok()).toBeTruthy();

  expect((await page.request.post('/api/v1/ui/session/organization', { headers, data: { id: home.id } })).ok()).toBeTruthy();
});
