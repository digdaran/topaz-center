// Number and date formatting for the Russian UI. All values from the API are numbers or ISO strings.

const nf = (digits: number) => new Intl.NumberFormat('ru-RU', { minimumFractionDigits: digits, maximumFractionDigits: digits });
const n0 = nf(0);
const n1 = nf(1);
const n2 = nf(2);

export type Num = number | string | null | undefined;

export const toNum = (v: Num): number | null => {
  if (v === null || v === undefined || v === '') return null;
  const n = typeof v === 'number' ? v : Number(v);
  return Number.isFinite(n) ? n : null;
};

/** 1 234 567 ₽ (no kopecks above 10 000 ₽, so tables stay readable). */
export const money = (v: Num, exact = false): string => {
  const n = toNum(v);
  if (n === null) return '—';
  return `${(exact || Math.abs(n) < 10000 ? n2 : n0).format(n)} ₽`;
};

export const liters = (v: Num): string => {
  const n = toNum(v);
  return n === null ? '—' : `${(Math.abs(n) < 1000 ? n2 : n0).format(n)} л`;
};

export const int = (v: Num): string => {
  const n = toNum(v);
  return n === null ? '—' : n0.format(n);
};

export const dec = (v: Num, digits = 2): string => {
  const n = toNum(v);
  return n === null ? '—' : nf(digits).format(n);
};

/** Share 0..1 → "43,8 %". */
export const pct = (v: Num): string => {
  const n = toNum(v);
  return n === null ? '—' : `${n1.format(n * 100)} %`;
};

/** Relative change of current vs previous: +12,3 %, or null when not comparable. */
export const change = (current: Num, previous: Num): number | null => {
  const c = toNum(current);
  const p = toNum(previous);
  if (c === null || p === null || p === 0) return null;
  return (c - p) / Math.abs(p);
};

/** Compact for chart axes: 1,2 млн / 350 тыс. */
export const compact = (v: number): string => {
  const a = Math.abs(v);
  if (a >= 1e6) return `${n1.format(v / 1e6)} млн`;
  if (a >= 1e4) return `${n0.format(v / 1e3)} тыс`;
  return n0.format(v);
};

const pad = (n: number) => String(n).padStart(2, '0');

/** UTC ISO timestamp → local time of the given IANA zone, "31.10.2026 21:59". */
export const dateTime = (iso: string | null | undefined, timeZone?: string): string => {
  if (!iso) return '—';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return new Intl.DateTimeFormat('ru-RU', {
    timeZone: timeZone ?? 'Europe/Moscow',
    day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit',
  }).format(d);
};

/** "2026-10-31" or "2026-10-31T00:00" bucket → "31.10.2026" / "31.10 14:00" / "окт 2026". */
export const bucketLabel = (bucket: string, granularity: string): string => {
  const [date, time] = bucket.split('T');
  const [y, m, d] = date.split('-').map(Number);
  if (granularity === 'month') return `${MONTHS[m - 1]} ${y}`;
  if (granularity === 'hour') return `${pad(d)}.${pad(m)} ${time?.slice(0, 5) ?? ''}`;
  if (granularity === 'week') return `нед. ${pad(d)}.${pad(m)}`;
  return `${pad(d)}.${pad(m)}.${y}`;
};

export const MONTHS = ['янв', 'фев', 'мар', 'апр', 'май', 'июн', 'июл', 'авг', 'сен', 'окт', 'ноя', 'дек'];
export const WEEKDAYS = ['Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Вс'];

export const duration = (hours: Num): string => {
  const h = toNum(hours);
  if (h === null) return '—';
  const whole = Math.floor(h);
  return `${whole} ч ${pad(Math.round((h - whole) * 60))} мин`;
};

export const PAYMENT_KIND: Record<string, string> = {
  cash: 'Наличные',
  cashless: 'Безнал',
  mixed: 'Смешанная',
  none: 'Без оплаты',
};
