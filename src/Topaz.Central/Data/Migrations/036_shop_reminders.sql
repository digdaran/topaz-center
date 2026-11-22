-- 1.25: Telegram reminders of the shop purchasing.
-- shop_purchase_due: on a trip day (purchase_settings.trip_weekdays; only organizations that set their trips), from
--   the threshold hour of the station's local time: what to buy today. One alert per station and day; computed by
--   AnomalyDetector with PurchasePlanner; closes once a list with the station's goods is marked "bought" or the day is over.
-- shop_core_out: an item marked "always in stock" ran out. One alert per run-out (key: item and the move that emptied
--   it); closes when the item is back in stock.
INSERT INTO alert_rules (code, title, description, severity, threshold, unit, sort) VALUES
 ('shop_purchase_due', 'Закупка сегодня',
  'Утром в день поездки за товаром (дни задаются кнопкой «Поездки» в «Склад → Закупка»): сколько позиций купить и чего нет на полке. Порог — час по времени АЗС',
  'warning', 8, 'ч', 31),
 ('shop_core_out', 'Закончился товар «всегда в наличии»',
  'Остаток товара с отметкой «Всегда в наличии» дошёл до нуля',
  'warning', NULL, NULL, 32);
