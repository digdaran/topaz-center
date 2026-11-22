-- 1.24: a shopping list marked "bought" whose goods Topaz has not received (no incoming bill) for the threshold days:
-- the stocks and the next list stay wrong until the receipt is entered. One alert per list and station; closes itself.
INSERT INTO alert_rules (code, title, description, severity, threshold, unit, sort) VALUES
 ('shop_purchase_not_received', 'Куплено, но не оприходовано',
  'Список закупки отмечен «Куплено», а прихода этих товаров в Топазе нет дольше порога: остатки и следующий список будут неверны',
  'warning', 2, 'дн.', 30);
