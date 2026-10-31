# База данных «Топаз АЗС»: что известно

Изучена копия рабочей базы одной АЗС (2026-10-31): `ProgConfig.Version = 3.15.20.12096`, Firebird **2.5.9**, ODS 11.2, кодировка UTF8, страница 8 КБ.
Сам файл базы в репозиторий **не попадает** (`*.fdb` в `.gitignore`): в нём реальные данные АЗС.

## Общие особенности

- **117 пользовательских таблиц**, 23 процедуры, 112 генераторов, около 400 триггеров.
- Имена в **смешанном регистре в кавычках**: `"flOrders"`, `"SessionID"`. Без кавычек запрос не сработает.
- Префиксы: `dc*` — справочники, `fl*` — топливо, `tr*` — товарные документы, `rg*` — регистры (движения), `cm*` — касса, `sys*` — системные таблицы и журнал.
- Домены: `TID` = INTEGER (ключи), `TCurrency`/`TVolume` = NUMERIC(15,2), `TQuantity` = NUMERIC(15,3), `TDensity` = NUMERIC(15,4), `TBoolean` = INTEGER 0/1, `TDateTime` = TIMESTAMP (**местное время без пояса**), `TXmlBlob` = BLOB TEXT.
- В транзакционных таблицах **нет даты изменения**. `modifyDate` есть только у части справочников, и это служебное поле синхронизации с офисом.
- Строки меняются после вставки: заказ `flOrders` создаётся при старте налива и обновляется по окончании.
- ID в `flOrders` и `sysEvents` растут вместе со временем (проверено на всех строках).
- Триггеры Топаза вызывают UDF (`MD5`), поэтому вставка или изменение через сторонний клиент без библиотек Топаза падает. Агенту это не мешает: он только читает.
- Строк, которые ссылаются на несуществующую смену, нет (проверено для `flOrders`, `rgCashes`, `rgFuels`, `trFastTradeH`).

## Ключевые таблицы

| Таблица | PK | Смысл |
|---|---|---|
| `sysSessions` | `SessionID` | смена: `SessionNum`, `UserName`, `StartDateTime`, `EndDateTime` (NULL — открыта) |
| `sysLocalSessions` | `LocalSessionID` | смена рабочего места (`SessionID`, `WorkbenchID`) |
| `flOrders` | `OrderID` | отпуск топлива: `SessionID`, `DateTime`, `FuelID`, `TankID`, `HoseID`, `PaymentModeID`, `Volume`, `Price`, `Amount`, `AmountCash/Cashless/Discount/Bonuses`, `CardCode`, `FiscalNumber`, `StatusOrder` |
| `flOrdersExtInfo` | `PumpInfoID` | данные ТРК по заказу (`OrderID` → `flOrders`) |
| `flSesTanks` | `SessionID, TankID` | резервуар за смену: начало, приход, отпуск, факт, потери, плотность, температура, уровень |
| `flSesHoses` | `SessionID, HoseID` | счётчики рукавов на начало и конец смены |
| `flRecepts` | `ReceptID` | приём топлива |
| `trFastTradeH` / `trFastTradeD` | `FastTradeHID` / `FastTradeDID` | чек магазина и его строки (`FastTradeHID` → H) |
| `rgFuels` | `FuelRegID` | регистр движения топлива (`DocTypeID` — тип документа из `sysDocTypes`) |
| `rgCashes` | `RgCashID` | регистр денег по кассам |
| `rgItemRests` | `rgItemRestID` | регистр товаров: сумма `Quantity` по `StorageID`/`ItemID` = остаток |
| `sysEvents` | `EventID` | журнал событий (`EventCategoryID` → `sysEventCategores`) |
| `dcFuels`, `dcTanks`, `dcPumps`, `dcHoses` | | топливо, резервуары, ТРК, рукава |
| `dcPaymentModes` | `PaymentModeID` | виды оплаты (-1 внешний, 0 переливы, 1 наличные, 91 безнал, 92 ведомости, …) |
| `dcItems`, `dcItemsGrps`, `dcUnits`, `dcItemPrices`, `dcStorages` | | номенклатура, группы, единицы, цены, склады |
| `ProgConfig` | `Name` | настройки: `AZSCode`, `AZSName`, `Version`, … |

`sysDocTypes`: 1 приходная накладная, 2 расходная, 3 ввод остатков товаров, 4 перемещение, 5 чек быстрой продажи, 6 отпуск топлива, 7 приём топлива, 8 ввод остатков взаиморасчётов, 9–10 приём и отпуск через стояки, 11 технический возврат, 12 оприходование излишков, 13 списание потерь, 14–15 внесение и выдача наличных, 16 отпуск газа, 17–18, 21 операции с картами, 19–20 акты оприходования и списания ТМЦ.

## Что выгружается

Полный список — `src/Topaz.Agent/datasets.json` (105 таблиц). Его можно править без пересборки агента: таблица, которой нет в другой версии Топаза, пропускается с предупреждением в логе.

**Не выгружаются:** служебные таблицы синхронизации и журналы изменений (`sysDataChangeLogs`, `sysDataChangeLogTables`, `sysDataChangeTypes`, `syncData`, `syncGlobalErrors`, `sysExportExceptions`), служебные (`sysModules`, `sysConsts`, `sysDescriptions`, `sysUsersLaw`), таблицы без PK (`rgDCWebOfficeTransaction`, `rgOEMediaTransaction`).

**Исключённые колонки:** `dcAgents.Password`, `sysUsers.PswHash`, `sysUsers.Psw`, `sysUsers.SecurityData`, `dcPaymentModes.Config`.

## Открытые вопросы (сверить на пилоте)

- Значения `flOrders.StatusOrder`, `OrderSource`, `CardType`: в изученной базе все `StatusOrder = 0`.
- Нужен ли `CardCode` (номер топливной карты) в центре в открытом виде. Сейчас он передаётся как есть по TLS, в логи не пишется.
- Можно ли открыть доступ к Firebird АЗС по сети (сейчас агент ставится на тот же ПК и ходит на `127.0.0.1:3050`).
