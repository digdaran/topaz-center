/** Payment categories of a sale (server: payment_category_guess / v_payment_mode_flags). */
export type PayCategory = 'cash' | 'card' | 'contract' | 'fuel_card' | 'none';

export const PAY: Record<PayCategory, { label: string; short: string; color: string }> = {
  cash: { label: 'Наличные', short: 'Наличные', color: 'green' },
  card: { label: 'Банковская карта', short: 'Карты', color: 'blue' },
  contract: { label: 'По договору (ведомости)', short: 'Договоры', color: 'purple' },
  fuel_card: { label: 'Топливные карты', short: 'Топл. карты', color: 'gold' },
  none: { label: 'Без оплаты', short: 'Без оплаты', color: 'default' },
};

/** Values of the "Способ оплаты" filter (unpaid dispensing is not a filter value). */
export const PAY_FILTER: PayCategory[] = ['cash', 'card', 'contract', 'fuel_card'];

export const payLabel = (v: string | null | undefined) => (v && v in PAY ? PAY[v as PayCategory].label : v ?? '—');

/**
 * Why a sale's payment method differs from its payment mode (server: sale_pay_category, 033): Topaz's till saw the
 * money (a declined card paid in cash, a "Базовый" mode) or did not (a cash mode paid by card).
 */
export function tillNote(category: string, modeCategory: string | null | undefined, till: string | null | undefined): string | null {
  if (!modeCategory || modeCategory === category) return null;
  if (till === 'in' && category === 'cash') return 'Деньги прошли через кассу Топаза наличными, хотя вид оплаты безналичный — например, карта не прошла и клиент заплатил наличными';
  if (till === 'out' && category === 'card') return 'Вид оплаты наличный, но деньги не прошли через кассу Топаза — оплачено картой';
  return null;
}
