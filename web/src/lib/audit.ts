/** «Журнал действий» in the owner's words (codes written by Audit.WriteAsync on the server). */
export const AUDIT_ACTION: Record<string, string> = {
  login: 'Вход в сервис',
  login_failed: 'Неверный пароль при входе',
  login_code_failed: 'Неверный код при входе',
  login_passkey_failed: 'Не удался вход по отпечатку',
  password_changed: 'Сменил свой пароль',
  totp_enabled: 'Включил двухфакторную защиту',
  totp_disabled: 'Выключил двухфакторную защиту',
  totp_reset: 'Сбросил двухфакторную защиту',
  passkey_added: 'Добавил вход по отпечатку',
  passkey_removed: 'Удалил вход по отпечатку',
  user_created: 'Добавил пользователя',
  user_updated: 'Изменил пользователя',
  organization_created: 'Создал организацию',
  organization_updated: 'Изменил организацию',
  organization_switched: 'Переключился на организацию',
  organization_settings_updated: 'Изменил настройки маржи и НДС',
  station_updated: 'Изменил настройки АЗС',
  station_moved: 'Передал АЗС другой организации',
  station_access_block: 'Заблокировал АЗС',
  station_access_unblock: 'Разблокировал АЗС',
  station_access_reissue: 'Перевыпустил доступ АЗС',
  station_registration_approved: 'Одобрил подключение АЗС',
  station_registration_rejected: 'Отклонил подключение АЗС',
  station_registration_code_mismatch: 'Код подтверждения АЗС не совпал',
  station_update_settings: 'Изменил обновление агента АЗС',
  agent_updates: 'Изменил обновление агентов',
  payment_mode_updated: 'Изменил вид оплаты',
  fuel_type_mapped: 'Сопоставил топливо АЗС',
  fuel_types_confirmed: 'Подтвердил виды топлива',
  fuel_type_created: 'Добавил вид топлива в справочник',
  fuel_type_updated: 'Изменил вид топлива в справочнике',
  fuel_type_deleted: 'Удалил вид топлива из справочника',
  fuel_cost_added: 'Ввёл цену топлива',
  fuel_cost_deleted: 'Удалил цену топлива',
  item_cost_added: 'Ввёл себестоимость товара',
  item_cost_deleted: 'Удалил себестоимость товара',
  item_costs_imported: 'Загрузил себестоимость из Excel',
  plan_set: 'Задал план продаж',
  tank_settings: 'Изменил настройки резервуара',
  alert_rule_updated: 'Изменил правило контроля',
  alert_rule_reset: 'Вернул правило к общему значению',
  alert_rule_default_updated: 'Изменил общее значение правила',
  channel_added: 'Добавил чат Telegram',
  channel_deleted: 'Удалил чат Telegram',
  telegram_linked: 'Подключил Telegram',
  telegram_unlinked: 'Отключил Telegram',
};

/** Failed logins and code mismatches: shown in red. */
export const AUDIT_WARN = new Set(['login_failed', 'login_code_failed', 'login_passkey_failed', 'station_registration_code_mismatch']);

const ENTITY: Record<string, string> = {
  user: 'Пользователь', station: 'АЗС', organization: 'Организация', rule: 'Правило', channel: 'Чат Telegram', registration: 'Заявка АЗС',
  fuel_type: 'Вид топлива', telegram: 'Telegram', tank: 'Резервуар', plan: 'План', cost: 'Цена', item: 'Товар', payment_mode: 'Вид оплаты',
};

const DETAIL: Record<string, string> = {
  ip: 'IP', name: 'название', login: 'логин', role: 'роль', stations: 'АЗС', method: 'способ', kind: 'вид', title: 'название',
  enabled: 'включено', threshold: 'порог', severity: 'важность', min_severity: 'важность от', daily_digest: 'сводка в 9:00',
  organization_id: 'организация', is_active: 'активен', password_changed: 'пароль сменён', totp_reset: 'защита сброшена',
  category: 'способ оплаты', is_discount: 'дисконтный', fuel_type: 'вид топлива', count: 'количество', from: 'было', to: 'стало',
  action: 'действие', margin_vat: 'маржа', vat_rate: 'ставка НДС', value: 'значение', month: 'месяц', metric: 'показатель',
};

const ROLE: Record<string, string> = { superadmin: 'суперадминистратор', owner: 'владелец', manager: 'управляющий', viewer: 'просмотр' };

export const auditAction = (code: string) => AUDIT_ACTION[code] ?? code.replace(/_/g, ' ');

/** "station:<id>" → "АЗС «Центр»" when the name is known (names: id → name). */
export function auditEntity(entity: string | null, names: Record<string, string>): string {
  if (!entity) return '';
  const i = entity.indexOf(':');
  if (i < 0) return entity;
  const type = entity.slice(0, i), id = entity.slice(i + 1);
  const label = ENTITY[type] ?? type;
  if (names[id]) return `${label} «${names[id]}»`;
  if (type === 'rule' || type === 'fuel_type' || type === 'telegram') return `${label}: ${id}`;
  return label;
}

/** The details (a JSON object, sometimes stored as a JSON string) as short "ключ: значение" pairs. */
export function auditDetails(details: unknown, names: Record<string, string>): string {
  let d = details;
  if (typeof d === 'string') {
    try { d = JSON.parse(d); } catch { return d as string; }
  }
  if (d == null || typeof d !== 'object') return d == null ? '' : String(d);
  const value = (key: string, v: unknown): string | null => {
    const k = key.toLowerCase();
    if (v == null || v === '') return null;
    if (typeof v === 'boolean') return v ? 'да' : 'нет';
    if (k === 'role' && typeof v === 'string') return ROLE[v] ?? v;
    if (k === 'ip' && typeof v === 'string') return v.replace(/^::ffff:/, '');
    if (Array.isArray(v)) return v.length === 0 ? 'нет' : v.map((x) => names[String(x)] ?? String(x)).join(', ');
    if (typeof v === 'object') return JSON.stringify(v);
    return names[String(v)] ?? String(v);
  };
  return Object.entries(d as Record<string, unknown>)
    .map(([k, v]) => { const s = value(k, v); return s == null ? null : `${DETAIL[k.toLowerCase()] ?? k}: ${s}`; })
    .filter(Boolean).join(' · ');
}
