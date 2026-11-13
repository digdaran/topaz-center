/**
 * Addresses of the menu before 1.14 (bookmarks, links in old Telegram messages and notes) and where they live now.
 * The query string (period, stations, filters) goes along.
 */
const MOVED: Record<string, string> = {
  '/fuel': '/sales/fuel',
  '/demand': '/sales/demand',
  '/items': '/sales/shop',
  '/plans': '/sales/plans',
  '/finance': '/money/margin',
  '/costs': '/money/costs',
  '/tanks': '/stock/tanks',
  '/data': '/stations/data',
  '/settings/profile': '/profile',
  '/settings/organizations': '/platform/organizations',
};

/** Old «АЗС» tabs (?tab=…). */
const STATION_TABS: Record<string, string> = {
  connect: '/platform/connect',
  updates: '/platform/updates',
  compare: '/sales/stations',
  status: '/stations/status',
};

/** The new address of an old one, or null when the address is current. */
export function legacyPath(pathname: string, search: string): string | null {
  const path = pathname.length > 1 ? pathname.replace(/\/+$/, '') : pathname;
  const params = new URLSearchParams(search);
  if (path === '/stations' && params.has('tab')) {
    const to = STATION_TABS[params.get('tab')!] ?? '/stations/status';
    params.delete('tab');
    const rest = params.toString();
    return to + (rest ? `?${rest}` : '');
  }
  const to = MOVED[path];
  return to ? to + search : null;
}
