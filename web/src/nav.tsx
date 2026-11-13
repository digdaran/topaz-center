import {
  AlertOutlined, ClusterOutlined, DashboardOutlined, InboxOutlined, LineChartOutlined, QuestionCircleOutlined, ScheduleOutlined,
  SettingOutlined, ShopOutlined, WalletOutlined,
} from '@ant-design/icons';
import { ReactNode } from 'react';
import type { Me } from './api/client';

export type Role = Me['role'];

export interface NavTab {
  key: string;
  label: string;
  roles?: Role[];
}

/** A section of the menu: one owner's task (1.14). Tabs are addressed as `${path}/${key}`. */
export interface NavSection {
  path: string;
  label: string;
  icon: ReactNode;
  /** One line under the title: what this section is for. */
  description?: string;
  roles?: Role[];
  group: 'main' | 'service' | 'platform';
  tabs?: NavTab[];
}

/** Who sees money (margins, costs): not «просмотр». */
export const MONEY_ROLES: Role[] = ['superadmin', 'owner', 'manager'];
export const OWNER_ROLES: Role[] = ['superadmin', 'owner'];

export const SECTIONS: NavSection[] = [
  { path: '/', label: 'Сводка', icon: <DashboardOutlined />, group: 'main', description: 'Главные цифры за период и всё, что ждёт вашего решения' },
  { path: '/shifts', label: 'Смены', icon: <ScheduleOutlined />, group: 'main', description: 'Итоги каждой смены, как в отчёте Топаза, и работа операторов' },
  {
    path: '/sales', label: 'Продажи', icon: <LineChartOutlined />, group: 'main',
    description: 'Что, когда и как покупают: топливо, магазин, спрос по часам, сравнение АЗС и план',
    tabs: [
      { key: 'fuel', label: 'Топливо' },
      { key: 'shop', label: 'Магазин' },
      { key: 'demand', label: 'Спрос' },
      { key: 'stations', label: 'Сравнение АЗС' },
      { key: 'plans', label: 'План-факт' },
    ],
  },
  {
    path: '/money', label: 'Деньги', icon: <WalletOutlined />, group: 'main', roles: MONEY_ROLES,
    description: 'Маржа, деньги по дням, продажи по договорам и закупочные цены',
    tabs: [
      { key: 'margin', label: 'Маржа и деньги' },
      { key: 'contracts', label: 'Договоры' },
      { key: 'costs', label: 'Себестоимость' },
    ],
  },
  {
    path: '/stock', label: 'Склад', icon: <InboxOutlined />, group: 'main',
    description: 'Топливо в резервуарах — когда заказывать, приём и потери, — и остатки товаров',
    tabs: [
      { key: 'tanks', label: 'Резервуары' },
      { key: 'receipts', label: 'Приём топлива' },
      { key: 'losses', label: 'Потери' },
      { key: 'items', label: 'Товары' },
    ],
  },
  {
    path: '/control', label: 'Контроль', icon: <AlertOutlined />, group: 'main',
    description: 'События, которые сервис нашёл в данных, правила проверки и уведомления в Telegram',
    tabs: [
      { key: 'alerts', label: 'События' },
      { key: 'rules', label: 'Правила', roles: OWNER_ROLES },
      { key: 'telegram', label: 'Telegram' },
    ],
  },
  {
    path: '/stations', label: 'АЗС', icon: <ShopOutlined />, group: 'main',
    description: 'Связь с АЗС, их компьютеры и исходные данные из Топаза',
    tabs: [
      { key: 'status', label: 'Состояние' },
      { key: 'data', label: 'Исходные данные' },
    ],
  },
  {
    path: '/settings', label: 'Настройки', icon: <SettingOutlined />, group: 'service', roles: OWNER_ROLES,
    description: 'Сотрудники и их доступ, справочники и правила расчёта маржи',
    tabs: [
      { key: 'users', label: 'Пользователи' },
      { key: 'payment-modes', label: 'Виды оплаты' },
      { key: 'fuels', label: 'Виды топлива' },
      { key: 'organization', label: 'Маржа и НДС' },
      { key: 'audit', label: 'Журнал действий' },
    ],
  },
  { path: '/help', label: 'Помощь', icon: <QuestionCircleOutlined />, group: 'service' },
  {
    path: '/platform', label: 'Платформа', icon: <ClusterOutlined />, group: 'platform', roles: ['superadmin'],
    description: 'Для всего сервера: организации, подключение новых АЗС и обновление агентов',
    tabs: [
      { key: 'organizations', label: 'Организации' },
      { key: 'connect', label: 'Подключение АЗС' },
      { key: 'updates', label: 'Обновление агентов' },
    ],
  },
];

export const allowed = (roles: Role[] | undefined, role: Role) => !roles || roles.includes(role);

export const section = (path: string) => SECTIONS.find((s) => s.path === path)!;

/** The section a path belongs to (the longest matching prefix; "/" only for itself). */
export function sectionOf(pathname: string): NavSection | undefined {
  return SECTIONS.filter((s) => (s.path === '/' ? pathname === '/' : pathname === s.path || pathname.startsWith(s.path + '/')))
    .sort((a, b) => b.path.length - a.path.length)[0];
}
