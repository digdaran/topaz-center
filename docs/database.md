# Центральная БД (PostgreSQL 16)

Миграции лежат в `src/Topaz.Central/Data/Migrations/NNN_*.sql`. Сервис применяет их при старте (advisory lock, таблица `schema_migrations`). Новые изменения — только новым файлом с номером, уже применённые файлы не правятся.

## Слои

```
raw_batches  →  records (+ record_versions)  →  нормализованные таблицы и представления
 как пришло      текущее состояние каждой         fuel_sales, shifts, …  /  v_fuels, v_item_stock, …
                 строки каждой таблицы Топаза
```

### Служебные таблицы

| Таблица | Ключ | Назначение |
|---|---|---|
| `stations` | `id uuid`; `name_normalized` уникально | АЗС: название, `token_hash` (SHA-256), `status` (`active` / `reenroll` / `revoked`), `last_seen_at`, `agent_version`, `last_heartbeat jsonb` |
| `raw_batches` | `id bigserial`; **`UNIQUE (station_id, batch_id)`** — ключ идемпотентности | пакет как пришёл: `seq`, `schema_version`, `created_at` (часы агента), `received_at` (часы сервера), `payload jsonb`, `payload_sha256`, `status` (`received` / `processed` / `failed`), `attempts`, `error`, `processed_at`. `payload` очищается через `RawRetentionDays` (90), сама строка остаётся для аудита. |
| `records` | `(station_id, dataset, key)` | текущее состояние строки любой выгружаемой таблицы: `session_id`, `hash`, `data jsonb`, `deleted` (мягкое удаление), `version`, `first_seen_at`, `updated_at`, `last_batch_id` |
| `record_versions` | `id`; индекс `(station_id, dataset, key, version)` | прежние состояния строк: при изменении или удалении строки сюда переносится её состояние до изменения (`upsert` с данными или `delete` без данных), `changed_at` — когда то состояние пришло, `raw_batch_id` — его пакет. Текущее состояние только в `records` (с 1.10.1; раньше сюда копировалась и каждая новая строка — такие дубли фоном удаляет `StationMonitor`, прогресс в `maintenance_state`). Видны правки уже закрытых смен. |

`dataset` — имя таблицы Топаза (`flOrders`, `sysEvents`, …), `key` — её первичный ключ (составной через `|`: `82|233`).

### Нормализованные таблицы

Заполняются в той же транзакции, что и `records` (см. `Processing/Projections.cs`). У всех таблиц первичный ключ `(station_id, <pk Топаза>)` и есть колонка `updated_at`. Время хранится как `timestamptz` (UTC).

| Таблица | Источник | Содержимое |
|---|---|---|
| `shifts` | `sysSessions` | смены: номер, оператор, начало и конец (`ended_at IS NULL` — смена открыта) |
| `fuel_sales` | `flOrders` | отпуск топлива: время, топливо, резервуар, рукав, вид оплаты, объём, цена, суммы (нал / безнал / скидка / бонусы), масса, плотность, карта, фискальный номер |
| `tank_shift_states` | `flSesTanks` | баланс резервуара за смену: начало, приход, отпуск, факт, потери и излишки, плотность, температура, уровень, вода |
| `hose_shift_counters` | `flSesHoses` | счётчики ТРК на начало и конец смены |
| `fuel_receipts` | `flRecepts` | приём топлива: документ, объёмы и масса по документу и по факту, расхождение |
| `shop_receipts` / `shop_receipt_lines` | `trFastTradeH` / `trFastTradeD` | чеки магазина и их строки |
| `station_events` | `sysEvents` | журнал событий Топаза |

### Представления

| Представление | Что показывает |
|---|---|
| `v_fuels`, `v_tanks`, `v_pumps`, `v_hoses`, `v_payment_modes`, `v_items`, `v_storages` | справочники прямо из `records` |
| `v_item_stock` | остатки товаров = сумма регистра `rgItemRests` по складу и товару |
| `v_fuel_sales` | `fuel_sales` с названиями АЗС, топлива и вида оплаты (для BI) |
| `v_station_status` | состояние АЗС: сколько молчит, очередь агента, ошибки, backlog на сервере |
| `v_agents`, `v_partners`, `v_cashes`, `v_doc_types` | справочники клиентов, контрагентов, касс, типов документов |
| `v_shift_fuel_outcomes` | ≙ раздел `OutcomesByRetail` отчёта смены Топаза: отпуск топлива по резервуару, рукаву, топливу, названию вида оплаты, `Remark`, агенту и карте |
| `v_shift_cash_flow` | ≙ `CashFlow`: движение денег по кассам за смену (регистр `rgCashes`) |
| `v_shift_item_sales` | ≙ `ItemOutcomesByRetail`: продажи товаров за смену |
| `item_rests_at(station, момент)` | ≙ `ItemRests`: остатки товаров на момент времени (в отчёте смены — на момент закрытия) |
| `v_shift_totals` | итоги смены: литры, выручка (наличные / безнал / смешанная), магазин, разница счётчиков ТРК |

Представления `v_shift_*` и `item_rests_at` сверены с реальным экспортом смены Топаза (0 расхождений), подробности в [topaz-schema.md](topaz-schema.md#сверка-с-отчётами-топаза-2026-10-31).

В `fuel_sales.payment_kind` хранится способ оплаты, как его проводит Топаз: `cashless`, если `AmountCashless > 0` (пометка «Б»), `cash`, `mixed` или `none` (`Amount = 0`: тех. прокачка, переливы, прерванный налив). Название вида оплаты для этого не годится.

Остальные таблицы Топаза доступны через `records` (`WHERE dataset = '…'`, поля через `data->>'Колонка'`) или через `/api/v1/export/{dataset}`.

## Примеры запросов

```sql
-- Продажи топлива по АЗС и видам топлива за вчера (в московском времени).
-- Как и в отчётах Топаза «за период», фильтр по времени операции, а не по сменам.
SELECT station, fuel_name, payment_kind, sum(volume) AS liters, sum(amount) AS rub
FROM v_fuel_sales
WHERE sold_at >= date_trunc('day', now() AT TIME ZONE 'Europe/Moscow') - interval '1 day'
  AND sold_at <  date_trunc('day', now() AT TIME ZONE 'Europe/Moscow')
GROUP BY 1, 2, 3 ORDER BY 1, 2, 3;

-- Баланс резервуаров по последней закрытой смене каждой АЗС
SELECT s.name, t.tank_num, t.fuel_name, t.start_volume, t.receipt_volume, t.pump_volume, t.fact_volume
FROM tank_shift_states t
JOIN stations s ON s.id = t.station_id
JOIN LATERAL (SELECT max(session_id) AS sid FROM shifts sh
              WHERE sh.station_id = t.station_id AND sh.ended_at IS NOT NULL) last ON last.sid = t.session_id;

-- Что изменилось задним числом: прежнее состояние (v) и текущее (r)
SELECT v.station_id, v.dataset, v.key, v.version, v.op, v.changed_at, r.updated_at AS replaced_at
FROM record_versions v JOIN records r USING (station_id, dataset, key)
ORDER BY r.updated_at DESC LIMIT 100;
```

## Доступ для BI (только чтение)

```sql
CREATE ROLE topaz_reader LOGIN PASSWORD '<секрет>';
GRANT CONNECT ON DATABASE topaz TO topaz_reader;
GRANT USAGE ON SCHEMA public TO topaz_reader;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO topaz_reader;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO topaz_reader;
REVOKE SELECT ON stations FROM topaz_reader;  -- хэши токенов
```

PostgreSQL находится во внутренней сети Docker. Для внешнего BI откройте порт через SSH-туннель или отдельный сервис с белым списком IP.

## Объёмы

На реальной АЗС за 8 месяцев набралось около 16 тыс. строк, из них примерно 7 тыс. — события. Для 20 АЗС это около 0,5 млн строк в год в `records` а в `record_versions` — только исправленные строки (заказ, дописанный после вставки, правки закрытых смен). Это небольшой объём для PostgreSQL: партиционирование не нужно. Если понадобится, `record_versions` и `raw_batches` можно партиционировать по месяцам.
