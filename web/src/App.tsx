import { BarChartOutlined, BulbOutlined, LogoutOutlined, MenuOutlined, SafetyOutlined, UserOutlined } from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { Alert, Badge, Button, Drawer, Dropdown, Flex, Grid, Layout, Menu, Select, Tag, Typography } from 'antd';
import { api, isOwner, isSuper, Me, useMeta } from './api/client';
import { ReactNode, Suspense, useEffect, useState } from 'react';
import { Navigate, Route, Routes, useLocation, useNavigate } from 'react-router-dom';
import { RequireAuth, useLogout, useMe } from './auth/auth';
import { useHelpUnseen } from './help/version';
import { useThemeMode } from './components/theme';
import { lazyPage, PageErrorBoundary, UpdateBanner, useReloadOnNavigateWhenOutdated, VersionLabel } from './components/appUpdate';
import { Spin } from 'antd';
import { pathAfterOrgSwitch } from './lib/orgSwitch';
import { legacyPath } from './lib/legacyPaths';
import { allowed, NavSection, SECTIONS, sectionOf } from './nav';

const LoginPage = lazyPage(() => import('./pages/LoginPage'));
const OverviewPage = lazyPage(() => import('./pages/OverviewPage'));
const ShiftsPage = lazyPage(() => import('./pages/ShiftsPage'));
const ShiftCardPage = lazyPage(() => import('./pages/ShiftCardPage'));
const SalesSection = lazyPage(() => import('./pages/SalesSection'));
const MoneySection = lazyPage(() => import('./pages/MoneySection'));
const StockSection = lazyPage(() => import('./pages/StockSection'));
const ControlPage = lazyPage(() => import('./pages/ControlPage'));
const StationsSection = lazyPage(() => import('./pages/StationsSection'));
const SettingsPage = lazyPage(() => import('./pages/SettingsPage'));
const PlatformPage = lazyPage(() => import('./pages/PlatformPage'));
const ProfilePage = lazyPage(() => import('./pages/ProfilePage'));
const HelpPage = lazyPage(() => import('./pages/HelpPage'));

/** A menu label with its counter on the right. */
const withBadge = (label: string, count: number | string | undefined, color: string, title?: string) =>
  count ? <Flex justify="space-between" align="center">{label}<Badge count={count} color={color} overflowCount={99} title={title} /></Flex> : label;

function Shell({ children }: { children: ReactNode }) {
  const me = useMe();
  const logout = useLogout();
  const navigate = useNavigate();
  const location = useLocation();
  const screens = Grid.useBreakpoint();
  const [drawer, setDrawer] = useState(false);
  const { mode, setMode } = useThemeMode();
  const selected = sectionOf(location.pathname)?.path ?? '';
  const alerts = useQuery({ queryKey: ['alerts-summary'], queryFn: () => api<{ counts: { new: number; critical: number } }>('/alerts/summary'), refetchInterval: 60_000 });
  const meta = useMeta();
  const unmapped = isOwner(me) ? meta.data?.unmapped_fuels ?? 0 : 0;
  const requests = isSuper(me) ? meta.data?.pending_registrations ?? 0 : 0;
  const unpriced = meta.data?.unpriced_receipts ?? 0;
  const helpNew = useHelpUnseen();
  const counts = alerts.data?.counts;
  const badge: Record<string, ReactNode> = {
    '/control': withBadge('Контроль', counts?.new, counts && counts.critical > 0 ? 'red' : 'orange', 'Новые события'),
    '/money': withBadge('Деньги', unpriced, 'orange', 'Сливы топлива без закупочной цены'),
    '/settings': withBadge('Настройки', unmapped, 'orange', 'Топливо АЗС ждёт сопоставления со справочником'),
    '/platform': withBadge('Платформа', requests, 'red', 'АЗС ожидают подключения'),
    '/help': withBadge('Помощь', helpNew ? 'новое' : undefined, 'blue', 'Вышло обновление — посмотрите «Что нового»'),
  };
  const visible = SECTIONS.filter((s) => allowed(s.roles, me.role));
  const item = (s: NavSection) => ({ key: s.path, icon: s.icon, label: badge[s.path] ?? s.label });
  const platform = visible.filter((s) => s.group === 'platform');
  const main = visible.filter((s) => s.group === 'main').map(item);
  const service = visible.filter((s) => s.group === 'service').map(item);
  // The superadmin console (own address): the platform comes first, the client's sections are "as the client".
  const items = me.console
    ? [
        ...platform.map(item),
        { type: 'divider' as const },
        { type: 'group' as const, label: 'Как клиент', children: main },
        { type: 'divider' as const },
        ...service,
      ]
    : [
        ...main,
        { type: 'divider' as const },
        ...service,
        ...(platform.length ? [{ type: 'group' as const, label: 'Сервер', children: platform.map(item) }] : []),
      ];
  // Report sections keep the period and stations; settings-like ones start clean.
  const keepsFilter = (path: string) => !['/settings', '/help', '/platform'].includes(path);
  const menu = (
    <Menu mode="inline" selectedKeys={[selected]} items={items} style={{ borderInlineEnd: 0 }}
      onClick={(e) => { navigate(e.key === '/help' && helpNew ? '/help/changes' : e.key + (keepsFilter(e.key) ? location.search : '')); setDrawer(false); }} />
  );
  const moved = legacyPath(location.pathname, location.search);
  const desktop = !!screens.lg;
  useReloadOnNavigateWhenOutdated();
  useConsoleLanding(me);
  const asClient = me.console && sectionOf(location.pathname)?.group === 'main';
  const brand = (
    <>
      <Typography.Text strong style={{ whiteSpace: 'nowrap', ...(desktop ? { fontSize: 16 } : {}) }}>АЗС Хаб</Typography.Text>
      {me.console && <Tag color="gold" style={{ marginInlineEnd: 0, paddingInline: 5 }}>консоль</Tag>}
    </>
  );

  return (
    <Layout style={{ minHeight: '100vh' }}>
      {desktop && (
        <Layout.Sider width={220} theme="light" style={{ position: 'sticky', top: 0, height: '100vh' }}>
          <Flex vertical style={{ height: '100%' }}>
            <Flex align="center" gap={8} wrap="wrap" style={{ padding: '16px 20px' }}>
              <BarChartOutlined style={{ fontSize: 22, color: '#1668dc' }} />
              {brand}
            </Flex>
            <div style={{ flex: 1, overflow: 'auto' }}>{menu}</div>
            <div style={{ padding: '8px 20px 12px' }}><VersionLabel /></div>
          </Flex>
        </Layout.Sider>
      )}
      <Layout>
        <Layout.Header style={{ padding: '0 16px', display: 'flex', alignItems: 'center', gap: 12, background: 'transparent', height: 56 }}>
          {!desktop && <Button icon={<MenuOutlined />} onClick={() => setDrawer(true)} aria-label="Меню" />}
          {!desktop && brand}
          <OrganizationBadge me={me} compact={!desktop} />
          <div style={{ flex: 1 }} />
          <Button
            type="text"
            icon={<BulbOutlined />}
            onClick={() => setMode(mode === 'dark' ? 'light' : mode === 'light' ? 'system' : 'dark')}
            title={`Тема: ${mode === 'dark' ? 'тёмная' : mode === 'light' ? 'светлая' : 'как в системе'}`}
          />
          <Dropdown
            menu={{
              items: [
                { key: 'role', label: `Роль: ${{ superadmin: 'суперадминистратор', owner: 'владелец', manager: 'управляющий', viewer: 'просмотр' }[me.role]}`, disabled: true },
                { key: 'profile', label: 'Профиль: пароль и защита входа', icon: <SafetyOutlined />, onClick: () => navigate('/profile') },
                { key: 'logout', label: 'Выйти', icon: <LogoutOutlined />, onClick: logout },
              ],
            }}
          >
            <Button type="text" icon={<UserOutlined />}>{desktop ? me.name : null}</Button>
          </Dropdown>
        </Layout.Header>
        <Layout.Content style={{ padding: desktop ? '8px 24px 24px' : '8px 16px 16px', maxWidth: 1600, width: '100%' }}>
          <UpdateBanner />
          {me.demo && (
            <Alert type="info" showIcon style={{ marginBottom: 12 }}
              message="Демо-версия: сеть из трёх вымышленных АЗС. Смотрите любые разделы — изменения здесь не сохраняются." />
          )}
          {asClient && (
            <Alert type="warning" showIcon style={{ marginBottom: 12 }}
              message={`Данные клиента «${me.organization ?? 'организация не выбрана'}» — вы смотрите их как администратор сервиса`}
              action={<Button size="small" onClick={() => navigate('/platform/organizations')}>В консоль</Button>} />
          )}
          <PageErrorBoundary resetKey={location.pathname}>{moved ? <Navigate to={moved} replace /> : children}</PageErrorBoundary>
        </Layout.Content>
      </Layout>
      <Drawer open={drawer} onClose={() => setDrawer(false)} placement="left" width={260} title="АЗС Хаб" styles={{ body: { padding: 0 } }}
        footer={<VersionLabel />}>
        {menu}
      </Drawer>
    </Layout>
  );
}

/** Opening the console lands on the platform once per tab; after that "/" is the client's «Сводка» as usual. */
function useConsoleLanding(me: Me) {
  const navigate = useNavigate();
  const location = useLocation();
  useEffect(() => {
    if (!me.console || location.pathname !== '/') return;
    let landed = false;
    try { landed = sessionStorage.getItem('topaz-console-landed') === '1'; sessionStorage.setItem('topaz-console-landed', '1'); } catch { /* private mode: land every time */ }
    if (!landed) navigate('/platform/organizations', { replace: true });
  }, [me.console, location.pathname, navigate]);
}

/**
 * Whose data is on screen: a superadmin picks the organization (one at a time; each switch is audited),
 * everyone else just sees the name of their own.
 */
function OrganizationBadge({ me, compact }: { me: Me; compact: boolean }) {
  if (!isSuper(me)) return me.organization ? <Tag style={{ marginInlineStart: 4 }}>{me.organization}</Tag> : null;
  const switchTo = async (id: string) => {
    await api('/session/organization', { method: 'POST', body: { id } });
    window.location.assign(pathAfterOrgSwitch(window.location.pathname)); // fresh data and filters of the other organization
  };
  return (
    <Select size="small" value={me.organization_id ?? undefined} placeholder="Организация" onChange={switchTo}
      style={{ minWidth: compact ? 120 : 220 }} popupMatchSelectWidth={false} showSearch optionFilterProp="label"
      options={(me.organizations ?? []).map((o) => ({ value: o.id, label: o.is_active ? o.name : `${o.name} (отключена)` }))} />
  );
}

const Loading = () => <Spin style={{ display: 'block', margin: '96px auto' }} size="large" />;

export default function App() {
  return (
    <Suspense fallback={<Loading />}>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route
          path="/*"
          element={
            <RequireAuth>
              <Shell>
                <Suspense fallback={<Loading />}>
                  <Routes>
                    <Route path="/" element={<OverviewPage />} />
                    <Route path="/shifts" element={<ShiftsPage />} />
                    <Route path="/shifts/:station/:session" element={<ShiftCardPage />} />
                    <Route path="/sales/:tab?" element={<SalesSection />} />
                    <Route path="/money/:tab?" element={<MoneySection />} />
                    <Route path="/stock/:tab?" element={<StockSection />} />
                    <Route path="/control/:tab?" element={<ControlPage />} />
                    <Route path="/stations/:tab?" element={<StationsSection />} />
                    <Route path="/settings/:tab?" element={<SettingsPage />} />
                    <Route path="/platform/:tab?" element={<PlatformPage />} />
                    <Route path="/profile" element={<ProfilePage />} />
                    <Route path="/help/*" element={<HelpPage />} />
                    <Route path="*" element={<Navigate to="/" replace />} />
                  </Routes>
                </Suspense>
              </Shell>
            </RequireAuth>
          }
        />
      </Routes>
    </Suspense>
  );
}
