/** Shop purchasing (Склад → Закупка): one line of the plan (server: PurchaseEndpoints.LineJson). */
export interface PurchaseLine {
  station_id: string; station: string; item_id: number; item_key: string; name: string; group_name: string | null;
  mode: 'auto' | 'core' | 'stop'; abc: string; stock: number; on_order: number; per_day: number; days_of_cover: number | null;
  in_stock_days: number; zero_days: number; low_data: boolean; safety: number; target: number; pack: number; pack_learned: boolean;
  qty: number; price: number | null; amount: number | null; status: 'out' | 'low' | 'refill' | 'ok' | 'stop';
  min_qty: number | null; max_qty: number | null; note: string | null; last_sale: string | null;
}

export const MODE: Record<PurchaseLine['mode'], { label: string; short: string; color: string }> = {
  auto: { label: 'По продажам', short: 'по продажам', color: 'default' },
  core: { label: 'Всегда в наличии', short: 'всегда в наличии', color: 'green' },
  stop: { label: 'Не закупать', short: 'не закупать', color: 'red' },
};

export const STATUS: Record<string, { label: string; color: string }> = {
  out: { label: 'нет на полке', color: 'red' },
  low: { label: 'не хватит до поездки', color: 'orange' },
  refill: { label: 'пополнить', color: 'blue' },
  ok: { label: 'хватает', color: 'green' },
  stop: { label: 'не закупаем', color: 'default' },
};

export const WEEKDAYS = ['Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Вс'];
const WEEKDAY_NAMES = ['воскресенье', 'понедельник', 'вторник', 'среда', 'четверг', 'пятница', 'суббота'];

/** "2026-11-23" → "23.11, понедельник". */
export function dayLabel(iso: string): string {
  const [y, m, d] = iso.slice(0, 10).split('-').map(Number);
  return `${String(d).padStart(2, '0')}.${String(m).padStart(2, '0')}, ${WEEKDAY_NAMES[new Date(Date.UTC(y, m - 1, d)).getUTCDay()]}`;
}

export const lineKey = (l: { station_id: string; item_id: number }) => `${l.station_id}/${l.item_id}`;

/** The trip's budget with the owner's edited quantities (lines without a price are left out). */
export function sumAmount(lines: PurchaseLine[], qty: Record<string, number>): number {
  return Math.round(lines.reduce((s, l) => s + (l.price ?? 0) * (qty[lineKey(l)] ?? l.qty), 0) * 100) / 100;
}
