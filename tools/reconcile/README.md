# Сверка с экспортами Топаза

Скрипты сравнивают данные центральной БД с XML-экспортами самого Топаза. Пригодятся при подключении новой АЗС или после обновления версии Топаза. Нужен только Python 3, без сторонних пакетов. Экспорты кладите в `samples/` (папка в `.gitignore`).

```bash
DB=topaz   # база центрального сервиса
# 1. снимок records (одна строка JSON на запись)
psql -d $DB -qAtc "select json_build_object('dataset',dataset,'key',key,'deleted',deleted,'data',data) from records where station_id = '<id АЗС>'" > records.ndjson

# 2. построчная выгрузка за период (Топаз: экспорт данных → DataPacket XML)
python3 -I tools/reconcile/period_export.py "samples/Экспорт за период.XML" records.ndjson src/Topaz.Agent/datasets.json details.json

# 3. агрегированный экспорт смены (Type="Aggregated"): пересчёт каждого раздела из строк
python3 -I tools/reconcile/shift_report.py "samples/экспорт смена.xml" records.ndjson

# 4. те же цифры из SQL-представлений центра (подставьте номер смены)
psql -d $DB -qAtc "select json_build_object(
  'fuel',  (select json_agg(v) from v_shift_fuel_outcomes v where session_id=80),
  'cash',  (select json_agg(v) from v_shift_cash_flow v where session_id=80),
  'items', (select json_agg(v) from v_shift_item_sales v where session_id=80),
  'rests', (select json_agg(r) from shifts s, item_rests_at(s.station_id, s.ended_at) r where s.session_id=80),
  'totals',(select row_to_json(t) from v_shift_totals t where session_id=80))" > views.json
python3 -I tools/reconcile/shift_views.py "samples/экспорт смена.xml" views.json
```

Расхождения печатаются строками `✗ <раздел> <поле>: Топаз=… БД=…`.

Если в БД несколько АЗС, отфильтруйте снимок по одной станции (`station_id`), иначе ключи разных АЗС перемешаются. Это же касается запроса к представлениям (добавьте условие `station_id = …`).
