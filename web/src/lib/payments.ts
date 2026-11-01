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
