import { useQuery } from '@tanstack/react-query';
import { useSearchParams } from 'react-router-dom';
import { noteServerVersion } from '../lib/appVersion';
import { Filter, filterQuery, parseFilter } from '../lib/filter';

export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message);
  }
}

/** Fetch wrapper: same-origin cookie, CSRF header, problem+json messages, 401 → login. */
export async function api<T>(path: string, init: { method?: string; body?: unknown } = {}): Promise<T> {
  const res = await fetch(`/api/v1/ui${path}`, {
    method: init.method ?? 'GET',
    credentials: 'same-origin',
    headers: { 'X-Topaz-Ui': '1', ...(init.body !== undefined ? { 'Content-Type': 'application/json' } : {}) },
    body: init.body !== undefined ? JSON.stringify(init.body) : undefined,
  });
  noteServerVersion(res.headers.get('X-Topaz-Version'));
  // Login steps answer 401 for a wrong password, code or passkey: the login page shows it, no redirect.
  if (res.status === 401 && !path.startsWith('/auth/login') && !path.startsWith('/auth/passkey/') && path !== '/auth/me') {
    window.location.assign(`/login?next=${encodeURIComponent(window.location.pathname + window.location.search)}`);
  }
  if (!res.ok) {
    let message = `Ошибка ${res.status}`;
    try {
      const p = await res.json();
      message = p.title ?? p.detail ?? message;
    } catch {
      /* not json */
    }
    throw new ApiError(res.status, message);
  }
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

/** The filter from the URL plus a setter that keeps other query params. */
export function useFilter(): [Filter, (patch: Partial<Filter>) => void] {
  const [params, setParams] = useSearchParams();
  const filter = parseFilter(params);
  const set = (patch: Partial<Filter>) => {
    const next = { ...filter, ...patch };
    const p = new URLSearchParams(filterQuery(next));
    for (const [k, v] of params) if (!['stations', 'from', 'to', 'g', 'compare', 'fuel', 'pay'].includes(k)) p.set(k, v);
    setParams(p, { replace: true });
  };
  return [filter, set];
}

/** GET an analytics endpoint with the current filter. */
export function useFiltered<T>(path: string, extra: Record<string, string | number | undefined> = {}, enabled = true) {
  const [filter] = useFilter();
  const qs = filterQuery(filter, extra);
  return useQuery({ queryKey: [path, qs], queryFn: () => api<T>(`${path}?${qs}`), enabled, placeholderData: (prev) => prev });
}

export const csvUrl = (path: string, filter: Filter, extra: Record<string, string | number | undefined> = {}) =>
  `/api/v1/ui${path}?${filterQuery(filter, { ...extra, format: 'csv' })}`;

export interface Me {
  id: string;
  login: string;
  name: string;
  role: 'superadmin' | 'owner' | 'manager' | 'viewer';
  sees_money: boolean;
  station_count: number;
  /** The organization the user works in (a superadmin: the one selected in the header). */
  organization_id: string | null;
  organization: string | null;
  /** Superadmin only: all organizations for the header switch. */
  organizations: { id: string; name: string; is_active: boolean }[] | null;
  /** Two-factor login (code from an authenticator app). */
  totp_enabled: boolean;
  /** Superadmin: mandatory, cannot be turned off. */
  totp_mandatory: boolean;
  /** Mandatory but not on yet: only the setup screen works until it is. */
  totp_setup_required: boolean;
  recovery_codes_left: number;
}

/** Owner rights (a superadmin has them too). */
export const isOwner = (me: { role: string }) => me.role === 'owner' || me.role === 'superadmin';
/** Station connection, agent updates, users and the audit log. */
export const isSuper = (me: { role: string }) => me.role === 'superadmin';

export interface StationMeta {
  id: string;
  name: string;
  time_zone: string;
  region: string | null;
  group_name: string | null;
}

export interface FuelTypeMeta { code: string; name: string; is_active: boolean; present: boolean }

export interface Meta {
  stations: StationMeta[];
  fuels: string[];
  /** reference fuel types; `present` = sold by the visible stations */
  fuel_types: FuelTypeMeta[];
  /** station fuels waiting for a confirmed reference type */
  unmapped_fuels: number;
  unpriced_receipts: number;
  /** connection requests of new agents (owners and superadmins) */
  pending_registrations: number;
  item_groups: string[];
  payment_modes: string[];
  data_range: { first_sale: string | null; last_sale: string | null };
}

export const useMeta = () => useQuery({ queryKey: ['meta'], queryFn: () => api<Meta>('/meta'), staleTime: 60_000 });

/** Reference fuel type code → its current name (renames in the catalog show up everywhere). */
export function useFuelTypeName() {
  const meta = useMeta();
  const names = Object.fromEntries((meta.data?.fuel_types ?? []).map((t) => [t.code, t.name]));
  return (code: string) => names[code] ?? code;
}
