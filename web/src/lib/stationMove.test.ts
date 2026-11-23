import { describe, expect, it } from 'vitest';
import { movedMessage } from './stationMove';

describe('station move', () => {
  it('says what went along with the station', () => {
    expect(movedMessage(undefined)).toBe('АЗС передана');
    expect(movedMessage({ item_costs: 41, rule_overrides: 2, chats: 1, purchase_lists: 0 }))
      .toBe('АЗС передана. Вместе с ней перешли: 41 цена товара, 2 настройки правил, 1 Telegram-чат, настройки НДС и закупок');
    expect(movedMessage({ item_costs: 12, rule_overrides: 0, chats: 5, purchase_lists: 3 }))
      .toBe('АЗС передана. Вместе с ней перешли: 12 цен товаров, 5 Telegram-чатов, 3 списка закупки, настройки НДС и закупок');
  });
});
