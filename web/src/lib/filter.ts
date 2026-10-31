// The common analytics filter lives in the URL query string so any view can be shared by link.

export type Granularity = 'hour' | 'day' | 'week' | 'month';
export type Compare = 'prev' | 'yoy' | 'none';

export interface Filter {
  stations: string[];
  from: string; // yyyy-MM-dd, station-local
  to: string;
  g?: Granularity;
  compare: Compare;
  fuel?: string;
}

const iso = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;

export const today = () => iso(new Date());

export const defaultFilter = (now = new Date()): Filter => ({
  stations: [],
  from: iso(new Date(now.getFullYear(), now.getMonth(), 1)),
  to: iso(now),
  compare: 'prev',
});

const DATE = /^\d{4}-\d{2}-\d{2}$/;
const GRANULARITIES: Granularity[] = ['hour', 'day', 'week', 'month'];

export function parseFilter(params: URLSearchParams, now = new Date()): Filter {
  const d = defaultFilter(now);
  const from = params.get('from');
  const to = params.get('to');
  const g = params.get('g') as Granularity | null;
  const compare = params.get('compare') as Compare | null;
  const f: Filter = {
    stations: (params.get('stations') ?? '').split(',').filter(Boolean),
    from: from && DATE.test(from) ? from : d.from,
    to: to && DATE.test(to) ? to : d.to,
    g: g && GRANULARITIES.includes(g) ? g : undefined,
    compare: compare === 'yoy' || compare === 'none' ? compare : 'prev',
    fuel: params.get('fuel') || undefined,
  };
  if (f.to < f.from) [f.from, f.to] = [f.to, f.from];
  return f;
}

/** Query string for the API and the URL; empty values are omitted. */
export function filterQuery(f: Filter, extra: Record<string, string | number | undefined> = {}): string {
  const p = new URLSearchParams();
  if (f.stations.length) p.set('stations', f.stations.join(','));
  p.set('from', f.from);
  p.set('to', f.to);
  if (f.g) p.set('g', f.g);
  if (f.compare !== 'prev') p.set('compare', f.compare);
  if (f.fuel) p.set('fuel', f.fuel);
  for (const [k, v] of Object.entries(extra)) if (v !== undefined && v !== '') p.set(k, String(v));
  return p.toString();
}

/** Period presets for the date picker. */
export function presets(now = new Date()): { label: string; from: string; to: string }[] {
  const y = now.getFullYear();
  const m = now.getMonth();
  const day = (offset: number) => iso(new Date(y, m, now.getDate() + offset));
  const dow = (now.getDay() + 6) % 7; // Monday = 0
  return [
    { label: 'Сегодня', from: day(0), to: day(0) },
    { label: 'Вчера', from: day(-1), to: day(-1) },
    { label: 'Эта неделя', from: day(-dow), to: day(0) },
    { label: '7 дней', from: day(-6), to: day(0) },
    { label: 'Этот месяц', from: iso(new Date(y, m, 1)), to: day(0) },
    { label: 'Прошлый месяц', from: iso(new Date(y, m - 1, 1)), to: iso(new Date(y, m, 0)) },
    { label: '30 дней', from: day(-29), to: day(0) },
    { label: '90 дней', from: day(-89), to: day(0) },
    { label: 'С начала года', from: iso(new Date(y, 0, 1)), to: day(0) },
    { label: '12 месяцев', from: iso(new Date(y - 1, m, now.getDate() + 1)), to: day(0) },
  ];
}
