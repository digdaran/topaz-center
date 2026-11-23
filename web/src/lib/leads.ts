/** Stages of a site request (table leads, `stage`), in the order a request goes through them. */
export type LeadStage = 'new' | 'work' | 'connected' | 'refused';
export type RefuseReason = 'price' | 'not_topaz' | 'no_answer' | 'spam' | 'other';

export const LEAD_STAGES: { value: LeadStage; label: string; color: string }[] = [
  { value: 'new', label: 'Новая', color: 'gold' },
  { value: 'work', label: 'В работе', color: 'blue' },
  { value: 'connected', label: 'Подключена', color: 'green' },
  { value: 'refused', label: 'Отказ', color: 'default' },
];

export const REFUSE_REASONS: { value: RefuseReason; label: string }[] = [
  { value: 'price', label: 'Дорого' },
  { value: 'not_topaz', label: 'Не на Топазе' },
  { value: 'no_answer', label: 'Не дозвонился' },
  { value: 'spam', label: 'Спам' },
  { value: 'other', label: 'Другое' },
];

/** "Дорого", "Другое: уже есть свой сервис". */
export function refusalText(reason: RefuseReason | null, comment: string | null): string {
  const label = REFUSE_REASONS.find((r) => r.value === reason)?.label ?? 'Причина не указана';
  return comment ? `${label}: ${comment}` : label;
}
