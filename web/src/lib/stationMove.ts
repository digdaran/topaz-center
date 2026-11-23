/** Moving a station to another organization (superadmin): what the dialog says and what the result message says. */

export const MOVE_NOTE =
  'Вся история АЗС переходит к новой организации. Управляющие и наблюдатели прежней организации теряют к ней доступ. '
  + 'Общие цены товаров, настройки правил «Контроля», НДС, закупок и групповые Telegram-чаты остаются в прежней организации — '
  + 'кроме случая, когда это её последняя АЗС, а у новой организации АЗС ещё нет: тогда они переходят вместе с АЗС. '
  + 'Личные Telegram-чаты остаются у своих пользователей.';

export interface Carried { item_costs: number; rule_overrides: number; chats: number; purchase_lists: number }

const plural = (n: number, one: string, few: string, many: string) => {
  const m10 = n % 10, m100 = n % 100;
  return m10 === 1 && m100 !== 11 ? one : m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14) ? few : many;
};

/** "АЗС передана. Вместе с ней перешли: 41 цена товара, 2 настройки правил, 1 Telegram-чат". */
export function movedMessage(carried?: Carried | null): string {
  if (!carried) return 'АЗС передана';
  const parts = [
    carried.item_costs ? `${carried.item_costs} ${plural(carried.item_costs, 'цена товара', 'цены товаров', 'цен товаров')}` : '',
    carried.rule_overrides ? `${carried.rule_overrides} ${plural(carried.rule_overrides, 'настройка правил', 'настройки правил', 'настроек правил')}` : '',
    carried.chats ? `${carried.chats} Telegram-${plural(carried.chats, 'чат', 'чата', 'чатов')}` : '',
    carried.purchase_lists ? `${carried.purchase_lists} ${plural(carried.purchase_lists, 'список закупки', 'списка закупки', 'списков закупки')}` : '',
  ].filter(Boolean);
  return `АЗС передана. Вместе с ней перешли: ${parts.join(', ')}, настройки НДС и закупок`;
}
