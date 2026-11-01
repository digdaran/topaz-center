import {
  AimOutlined, AlertOutlined, AppstoreOutlined, ExperimentOutlined, BarChartOutlined, CalculatorOutlined, WalletOutlined, BulbOutlined, DashboardOutlined, DatabaseOutlined, FireOutlined, LineChartOutlined, LogoutOutlined, MenuOutlined,
  QuestionCircleOutlined, ScheduleOutlined, SettingOutlined, ShopOutlined, ShoppingOutlined, UserOutlined,
} from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { Badge, Button, Drawer, Dropdown, Flex, Grid, Layout, Menu, Select, Tag, Typography } from 'antd';
import { api, isOwner, isSuper, Me, useMeta } from './api/client';
import { lazy, ReactNode, Suspense, useState } from 'react';
import { Navigate, Route, Routes, useLocation, useNavigate } from 'react-router-dom';
import { RequireAuth, useLogout, useMe } from './auth/auth';
import { useHelpUnseen } from './help/version';
import { useThemeMode } from './components/theme';
import { Spin } from 'antd';

const LoginPage = lazy(() => import('./pages/LoginPage'));
const OverviewPage = lazy(() => import('./pages/OverviewPage'));
const ShiftsPage = lazy(() => import('./pages/ShiftsPage'));
const ShiftCardPage = lazy(() => import('./pages/ShiftCardPage'));
const DataPage = lazy(() => import('./pages/DataPage'));
const FuelPage = lazy(() => import('./pages/FuelPage'));
const DemandPage = lazy(() => import('./pages/DemandPage'));
const ItemsPage = lazy(() => import('./pages/ItemsPage'));
const FinancePage = lazy(() => import('./pages/FinancePage'));
const PlansPage = lazy(() => import('./pages/PlansPage'));
const CostsPage = lazy(() => import('./pages/CostsPage'));
const TanksPage = lazy(() => import('./pages/TanksPage'));
const ControlPage = lazy(() => import('./pages/ControlPage'));
const StationsPage = lazy(() => import('./pages/StationsPage'));
const SettingsPage = lazy(() => import('./pages/SettingsPage'));
const HelpPage = lazy(() => import('./pages/HelpPage'));

interface NavItem {
  key: string;
  label: string;
  icon: ReactNode;
  roles?: string[];
}

export const NAV: NavItem[] = [
  { key: '/', label: 'Обзор', icon: <DashboardOutlined /> },
  { key: '/fuel', label: 'Топливо', icon: <FireOutlined /> },
  { key: '/demand', label: 'Спрос', icon: <LineChartOutlined /> },
  { key: '/items', label: 'Товары', icon: <ShoppingOutlined /> },
  { key: '/finance', label: 'Финансы', icon: <WalletOutlined />, roles: ['superadmin', 'owner', 'manager'] },
  { key: '/plans', label: 'План-факт', icon: <AimOutlined /> },
  { key: '/tanks', label: 'Запасы топлива', icon: <ExperimentOutlined /> },
  { key: '/control', label: 'Контроль', icon: <AlertOutlined /> },
  { key: '/shifts', label: 'Смены', icon: <ScheduleOutlined /> },
  { key: '/stations', label: 'АЗС', icon: <ShopOutlined /> },
  { key: '/data', label: 'Данные', icon: <DatabaseOutlined /> },
  { key: '/costs', label: 'Себестоимость', icon: <CalculatorOutlined />, roles: ['superadmin', 'owner', 'manager'] },
  { key: '/settings', label: 'Настройки', icon: <SettingOutlined /> },
  { key: '/help', label: 'Помощь', icon: <QuestionCircleOutlined /> },
];

function Shell({ children }: { children: ReactNode }) {
  const me = useMe();
  const logout = useLogout();
  const navigate = useNavigate();
  const location = useLocation();
  const screens = Grid.useBreakpoint();
  const [drawer, setDrawer] = useState(false);
  const { mode, setMode } = useThemeMode();
  const selected = NAV.filter((n) => n.key !== '/' && location.pathname.startsWith(n.key)).map((n) => n.key)[0] ?? '/';
  const alerts = useQuery({ queryKey: ['alerts-summary'], queryFn: () => api<{ counts: { new: number; critical: number } }>('/alerts/summary'), refetchInterval: 60_000 });
  const meta = useMeta();
  const unmapped = isOwner(me) ? meta.data?.unmapped_fuels ?? 0 : 0;
  const requests = isSuper(me) ? meta.data?.pending_registrations ?? 0 : 0;
  const unpriced = meta.data?.unpriced_receipts ?? 0;
  const helpNew = useHelpUnseen();
  const items = NAV.filter((n) => !n.roles || n.roles.includes(me.role)).map((n) => ({
    key: n.key, icon: n.icon,
    label: n.key === '/control' && (alerts.data?.counts.new ?? 0) > 0
      ? <Flex justify="space-between" align="center">{n.label}<Badge count={alerts.data!.counts.new} color={alerts.data!.counts.critical > 0 ? 'red' : 'orange'} overflowCount={99} /></Flex>
      : n.key === '/stations' && requests > 0
        ? <Flex justify="space-between" align="center">{n.label}<Badge count={requests} color="red" overflowCount={99} title="АЗС ожидают подключения" /></Flex>
      : n.key === '/costs' && unpriced > 0
        ? <Flex justify="space-between" align="center">{n.label}<Badge count={unpriced} color="orange" overflowCount={99} title="Сливы топлива без закупочной цены" /></Flex>
      : n.key === '/help' && helpNew
        ? <Flex justify="space-between" align="center">{n.label}<Badge count="новое" color="blue" title="Вышло обновление — посмотрите «Что нового»" /></Flex>
      : n.key === '/settings' && unmapped > 0
        ? <Flex justify="space-between" align="center">{n.label}<Badge count={unmapped} color="orange" overflowCount={99} title="Топливо АЗС ждёт сопоставления со справочником" /></Flex>
        : n.label,
  }));
  const menu = (
    <Menu mode="inline" selectedKeys={[selected]} items={items} onClick={(e) => { navigate(e.key === '/help' && helpNew ? '/help/changes' : e.key + (e.key === '/settings' || e.key === '/help' ? '' : location.search)); setDrawer(false); }} style={{ borderInlineEnd: 0 }} />
  );
  const desktop = !!screens.lg;

  return (
    <Layout style={{ minHeight: '100vh' }}>
      {desktop && (
        <Layout.Sider width={220} theme="light" style={{ position: 'sticky', top: 0, height: '100vh', overflow: 'auto' }}>
          <Flex align="center" gap={8} style={{ padding: '16px 20px' }}>
            <BarChartOutlined style={{ fontSize: 22, color: '#1668dc' }} />
            <Typography.Text strong style={{ fontSize: 16 }}>Topaz Center</Typography.Text>
          </Flex>
          {menu}
        </Layout.Sider>
      )}
      <Layout>
        <Layout.Header style={{ padding: '0 16px', display: 'flex', alignItems: 'center', gap: 12, background: 'transparent', height: 56 }}>
          {!desktop && <Button icon={<MenuOutlined />} onClick={() => setDrawer(true)} aria-label="Меню" />}
          {!desktop && <Typography.Text strong>Topaz Center</Typography.Text>}
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
                { key: 'profile', label: 'Сменить пароль', icon: <AppstoreOutlined />, onClick: () => navigate('/settings/profile') },
                { key: 'logout', label: 'Выйти', icon: <LogoutOutlined />, onClick: logout },
              ],
            }}
          >
            <Button type="text" icon={<UserOutlined />}>{desktop ? me.name : null}</Button>
          </Dropdown>
        </Layout.Header>
        <Layout.Content style={{ padding: desktop ? '8px 24px 24px' : '8px 16px 16px', maxWidth: 1600, width: '100%' }}>{children}</Layout.Content>
      </Layout>
      <Drawer open={drawer} onClose={() => setDrawer(false)} placement="left" width={260} title="Topaz Center" styles={{ body: { padding: 0 } }}>
        {menu}
      </Drawer>
    </Layout>
  );
}

/**
 * Whose data is on screen: a superadmin picks the organization (one at a time; each switch is audited),
 * everyone else just sees the name of their own.
 */
function OrganizationBadge({ me, compact }: { me: Me; compact: boolean }) {
  if (!isSuper(me)) return me.organization ? <Tag style={{ marginInlineStart: 4 }}>{me.organization}</Tag> : null;
  const switchTo = async (id: string) => {
    await api('/session/organization', { method: 'POST', body: { id } });
    window.location.assign(window.location.pathname); // fresh data and filters of the other organization
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
                    <Route path="/fuel" element={<FuelPage />} />
                    <Route path="/demand" element={<DemandPage />} />
                    <Route path="/items" element={<ItemsPage />} />
                    <Route path="/finance" element={<FinancePage />} />
                    <Route path="/plans" element={<PlansPage />} />
                    <Route path="/costs" element={<CostsPage />} />
                    <Route path="/tanks" element={<TanksPage />} />
                    <Route path="/control" element={<ControlPage />} />
                    <Route path="/shifts" element={<ShiftsPage />} />
                    <Route path="/shifts/:station/:session" element={<ShiftCardPage />} />
                    <Route path="/stations" element={<StationsPage />} />
                    <Route path="/data" element={<DataPage />} />
                    <Route path="/settings/*" element={<SettingsPage />} />
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
