using System.Globalization;
using System.Net;
using System.Text;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Topaz.Central.Ui;

namespace Topaz.Central.Processing;

/// <summary>
/// Every 5 minutes: evaluates the alert rules (alert_rules) over the last 30 days and stores new alerts
/// (deduplicated by rule + station + object), then sends fresh ones to the Telegram channels and a daily digest.
/// </summary>
public sealed class AnomalyDetector(NpgsqlDataSource db, IHttpClientFactory http, IOptions<CentralOptions> options, ILogger<AnomalyDetector> log) : BackgroundService
{
    public static readonly Dictionary<string, int> SeverityRank = new() { ["info"] = 1, ["warning"] = 2, ["critical"] = 3 };
    private readonly SemaphoreSlim _runLock = new(1, 1);

    /// <summary>Runs a detection round soon (e.g. a station asked to be connected: notify without waiting 5 minutes).</summary>
    public void Wake() => _ = Task.Run(async () =>
    {
        try { await RunAsync(CancellationToken.None); }
        catch (Exception ex) { log.LogError(ex, "Anomaly detection failed"); }
    });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try { await RunAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Anomaly detection failed"); }
        } while (await Wait(timer, stoppingToken));
    }

    private static async Task<bool> Wait(PeriodicTimer t, CancellationToken ct)
    {
        try { return await t.WaitForNextTickAsync(ct); } catch (OperationCanceledException) { return false; }
    }

    /// <summary>One detection + notification round. Returns the number of new alerts.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        await _runLock.WaitAsync(ct);
        try
        {
            await using var c = await db.OpenConnectionAsync(ct);
            var found = 0;
            await Topaz.Central.Auth.Registrations.MaintainAsync(c);
            foreach (var sql in RuleSql)
                found += await c.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct, commandTimeout: 300));
            found += await LowFuelAsync(c, ct);
            // A station that is back online closes its open "offline" alert.
            await c.ExecuteAsync("""
                UPDATE alerts a SET status = 'closed', status_by = 'auto' FROM v_station_status v
                WHERE a.rule_code = 'station_offline' AND a.status <> 'closed' AND v.id = a.station_id AND v.silent_for < interval '5 minutes'
                """);
            // PC health alerts close once the problem is gone (fresh data only: a silent station keeps its alert).
            await c.ExecuteAsync("""
                UPDATE alerts a SET status = 'closed', status_by = 'auto'
                FROM stations s, v_station_disk d, v_station_rules r
                WHERE a.station_id = s.id AND d.station_id = s.id AND r.station_id = s.id AND r.code = a.rule_code AND a.status <> 'closed'
                  AND s.host_checked_at > now() - interval '15 minutes'
                  AND ((a.rule_code = 'clock_skew' AND abs(COALESCE(s.clock_skew_seconds, 0)) <= r.threshold * 60)
                    OR (a.rule_code = 'disk_low' AND COALESCE(d.db_disk_free, d.sys_disk_free, 0) >= r.threshold * 1073741824
                                                 AND COALESCE(d.sys_disk_free, d.db_disk_free, 0) >= r.threshold * 1073741824))
                """);
            // A connection request alert closes once the request is decided (or expired).
            await c.ExecuteAsync("""
                UPDATE alerts a SET status = 'closed', status_by = 'auto' FROM station_registrations g
                WHERE a.rule_code = 'station_registration' AND a.status <> 'closed' AND g.id = a.registration_id AND g.status <> 'pending'
                """);
            // Counter alerts the data no longer supports are removed (e.g. raised while the station's history was still
            // uploading and the previous shift was missing), so a real problem of the same shift and hose can still be raised.
            await c.ExecuteAsync("""
                DELETE FROM alerts a
                WHERE a.rule_code = 'counter_gap' AND a.status <> 'closed'
                  AND NOT EXISTS (SELECT 1 FROM v_hose_counter_gaps g JOIN v_station_rules r ON r.station_id = g.station_id AND r.code = 'counter_gap'
                                  WHERE g.station_id = a.station_id AND g.hose_id || '/' || g.session_id = a.dedupe_key AND g.gap > r.threshold)
                """);
            await c.ExecuteAsync("""
                DELETE FROM alerts a
                WHERE a.rule_code = 'counter_mismatch' AND a.status <> 'closed'
                  AND NOT EXISTS (SELECT 1 FROM v_hose_counter_mismatches m JOIN v_station_rules r ON r.station_id = m.station_id AND r.code = 'counter_mismatch'
                                  WHERE m.station_id = a.station_id AND m.hose_id || '/' || m.session_id = a.dedupe_key AND abs(m.by_counter - m.by_orders) > r.threshold)
                """);
            // A receipt alert closes once the receipt has a price.
            await c.ExecuteAsync("""
                UPDATE alerts a SET status = 'closed', status_by = 'auto'
                WHERE a.rule_code = 'receipt_without_cost' AND a.status <> 'closed'
                  AND EXISTS (SELECT 1 FROM fuel_costs fc WHERE fc.station_id = a.station_id AND fc.receipt_id::text = a.dedupe_key)
                """);
            // Fuel mapping alerts are keyed by the set of unconfirmed fuels: once that set changes or is empty, the old one closes.
            await c.ExecuteAsync("""
                UPDATE alerts a SET status = 'closed', status_by = 'auto'
                WHERE a.rule_code = 'fuel_unmapped' AND a.status <> 'closed' AND a.dedupe_key IS DISTINCT FROM
                      (SELECT string_agg(f.fuel_id::text, ',' ORDER BY f.fuel_id) FROM v_station_fuels f WHERE f.station_id = a.station_id AND f.needs_confirmation)
                """);
            await NotifyAsync(c, ct);
            if (found > 0) log.LogInformation("Anomaly detection: {Count} new alert(s)", found);
            return found;
        }
        finally { _runLock.Release(); }
    }

    private const string Insert = "INSERT INTO alerts (station_id, rule_code, dedupe_key, severity, title, details, value, session_id, occurred_at) ";
    /// <summary>The rule as the station's organization set it (v_station_rules: override or default). {1}: alias with station_id.</summary>
    private const string Rule = "JOIN v_station_rules r ON r.code = '{0}' AND r.enabled AND r.station_id = {1}.station_id";
    private const string Recent = "s.started_at > now() - interval '30 days'";

    private static readonly string[] RuleSql =
    [
        // Counter of a hose at the start of a shift is above its end in the previous shift: fuel dispensed between shifts.
        Insert + $"""
        SELECT h.station_id, 'counter_gap', h.hose_id || '/' || h.session_id, r.severity,
               'Разрыв счётчика рукава ' || h.hose_id || ': ' || fmt_num(h.gap) || ' л',
               format('Смена №%s: на начало смены %s, на конец предыдущей смены №%s — %s. Возможен отпуск мимо учёта.',
                      h.session_num, fmt_num(h.start_counter), h.session_num - 1, fmt_num(h.prev_end)),
               round(h.gap, 2), h.session_id, h.started_at
        FROM v_hose_counter_gaps h
        {string.Format(Rule, "counter_gap", "h")}
        WHERE h.gap > r.threshold AND h.started_at > now() - interval '30 days'
        ON CONFLICT DO NOTHING
        """,
        // Counter delta of a closed shift differs from the orders of that hose.
        Insert + $"""
        SELECT h.station_id, 'counter_mismatch', h.hose_id || '/' || h.session_id, r.severity,
               'Счётчик рукава ' || h.hose_id || ' расходится с заказами на ' || fmt_num(abs(h.by_counter - h.by_orders)) || ' л',
               format('Смена №%s: по счётчику %s л, по заказам %s л.', h.session_num, fmt_num(h.by_counter), fmt_num(h.by_orders)),
               round(h.by_counter - h.by_orders, 2), h.session_id, h.ended_at
        FROM v_hose_counter_mismatches h
        {string.Format(Rule, "counter_mismatch", "h")}
        WHERE abs(h.by_counter - h.by_orders) > r.threshold AND h.started_at > now() - interval '30 days'
        ON CONFLICT DO NOTHING
        """,
        // Aborted fuellings (volume 0) share of a closed shift.
        Insert + $"""
        SELECT x.station_id, 'aborted_share', x.session_id::text, r.severity,
               'Прерванные наливы: ' || x.aborted || ' из ' || x.total || ' (' || round(100.0 * x.aborted / x.total) || '%)',
               format('Смена №%s, оператор %s.', s.session_num, s.user_name), round(100.0 * x.aborted / x.total, 1), x.session_id, s.ended_at
        FROM (SELECT station_id, session_id, count(*) FILTER (WHERE volume = 0) AS aborted, count(*) AS total FROM fuel_sales GROUP BY 1, 2) x
        JOIN shifts s ON s.station_id = x.station_id AND s.session_id = x.session_id AND s.ended_at IS NOT NULL
        {string.Format(Rule, "aborted_share", "x")}
        WHERE x.aborted >= 5 AND 100.0 * x.aborted / x.total > r.threshold AND {Recent}
        ON CONFLICT DO NOTHING
        """,
        // Unpaid dispensing (tech pumping, overflow) in a closed shift.
        Insert + $"""
        SELECT x.station_id, 'service_volume', x.session_id::text, r.severity,
               'Отпуск без оплаты: ' || fmt_num(x.liters) || ' л за смену', format('Смена №%s, оператор %s, операций: %s.', s.session_num, s.user_name, x.ops),
               x.liters, x.session_id, s.ended_at
        FROM (SELECT station_id, session_id, sum(volume) AS liters, count(*) AS ops FROM fuel_sales WHERE payment_kind = 'none' AND volume > 0 GROUP BY 1, 2) x
        JOIN shifts s ON s.station_id = x.station_id AND s.session_id = x.session_id AND s.ended_at IS NOT NULL
        {string.Format(Rule, "service_volume", "x")}
        WHERE x.liters > r.threshold AND {Recent}
        ON CONFLICT DO NOTHING
        """,
        // Discounts share of fuel revenue in a closed shift.
        Insert + $"""
        SELECT x.station_id, 'discount_share', x.session_id::text, r.severity,
               'Скидки ' || fmt_num(round(100 * x.discounts / x.revenue, 1)) || '% выручки смены', format('Смена №%s, оператор %s: скидки %s ₽.', s.session_num, s.user_name, fmt_num(x.discounts)),
               round(100 * x.discounts / x.revenue, 2), x.session_id, s.ended_at
        FROM (SELECT station_id, session_id, sum(fuel_discount(amount_discount, price, price_pump, volume)) AS discounts, sum(amount) AS revenue FROM fuel_sales GROUP BY 1, 2) x
        JOIN shifts s ON s.station_id = x.station_id AND s.session_id = x.session_id AND s.ended_at IS NOT NULL
        {string.Format(Rule, "discount_share", "x")}
        WHERE x.revenue > 0 AND 100 * x.discounts / x.revenue > r.threshold AND {Recent}
        ON CONFLICT DO NOTHING
        """,
        // A sale whose money does not match the pump: the customer paid more than the order price, the amount is not
        // litres × pump price, or the price was lowered past Topaz's discounts at a payment mode that gives none.
        // A card discount (lower pump price at a discount payment mode, or recorded by Topaz) is not an alert.
        Insert + $"""
        SELECT x.station_id, 'price_mismatch', x.session_id::text, r.severity,
               'Цена отпуска не сходится: ' || x.orders || ' отпуск(ов)',
               format('Смена №%s: %s. Например, отпуск №%s: %s л, цена заказа %s ₽, на колонке %s ₽, сумма %s ₽.', s.session_num,
                      concat_ws(', ', CASE WHEN x.overcharge > 0 THEN 'переплата клиента — ' || x.overcharge END,
                                      CASE WHEN x.amount_off > 0 THEN 'сумма ≠ литры × цена колонки — ' || x.amount_off END,
                                      CASE WHEN x.bypass > 0 THEN 'скидка мимо скидок Топаза — ' || x.bypass END),
                      x.order_num, fmt_num(x.volume), fmt_num(x.price), fmt_num(x.price_pump), fmt_num(x.amount)),
               x.orders, x.session_id, s.started_at
        FROM (SELECT y.station_id, y.session_id, count(*) AS orders,
                     count(*) FILTER (WHERE y.overcharge) AS overcharge, count(*) FILTER (WHERE y.amount_off) AS amount_off,
                     count(*) FILTER (WHERE y.bypass) AS bypass,
                     (array_agg(y.order_num ORDER BY y.sold_at))[1] AS order_num, (array_agg(y.volume ORDER BY y.sold_at))[1] AS volume,
                     (array_agg(y.price ORDER BY y.sold_at))[1] AS price, (array_agg(y.price_pump ORDER BY y.sold_at))[1] AS price_pump,
                     (array_agg(y.amount ORDER BY y.sold_at))[1] AS amount
              FROM (SELECT f.station_id, f.session_id, f.order_num, f.sold_at, f.volume, f.price, f.price_pump, f.amount,
                           f.price_pump > f.price + rr.threshold AS overcharge,
                           abs(f.amount - round(f.volume * f.price_pump, 2)) > 1 AS amount_off,
                           f.price > f.price_pump + rr.threshold AND COALESCE(f.amount_discount, 0) = 0 AND NOT COALESCE(pm.is_discount, false) AS bypass
                    FROM fuel_sales f
                    JOIN v_station_rules rr ON rr.station_id = f.station_id AND rr.code = 'price_mismatch'
                    LEFT JOIN v_payment_mode_flags pm ON pm.station_id = f.station_id AND pm.payment_mode_id = f.payment_mode_id
                    WHERE f.price_pump > 0 AND f.volume > 0 AND f.payment_kind <> 'none' AND f.sold_at > now() - interval '31 days') y
              WHERE y.overcharge OR y.amount_off OR y.bypass
              GROUP BY 1, 2) x
        JOIN shifts s ON s.station_id = x.station_id AND s.session_id = x.session_id
        {string.Format(Rule, "price_mismatch", "x")}
        WHERE {Recent}
        ON CONFLICT DO NOTHING
        """,
        // A fuel receipt still without a purchase price after the threshold (hours): its litres go at the tank's average.
        Insert + """
        SELECT f.station_id, 'receipt_without_cost', f.receipt_id::text, r.severity,
               'Не введена цена слива: ' || COALESCE(sf.fuel_name, 'топливо') || ', ' || fmt_num(f.doc_volume) || ' л',
               format('Слив %s, резервуар %s, документ %s. Введите закупочную цену в разделе «Себестоимость» — пока её нет, слив считается по средней цене резервуара.',
                      to_char(f.received_at AT TIME ZONE sn.time_zone, 'DD.MM.YYYY HH24:MI'), COALESCE(t.num::text, f.tank_id::text), COALESCE(NULLIF(f.doc_number, ''), 'без номера')),
               f.doc_volume, f.session_id, f.received_at
        FROM fuel_receipts f
        JOIN v_station_rules r ON r.code = 'receipt_without_cost' AND r.enabled AND r.station_id = f.station_id
        JOIN v_station_names sn ON sn.station_id = f.station_id
        LEFT JOIN v_station_fuels sf ON sf.station_id = f.station_id AND sf.fuel_id = f.fuel_id
        LEFT JOIN v_tanks t ON t.station_id = f.station_id AND t.tank_id = f.tank_id
        WHERE f.received_at > now() - interval '30 days' AND f.received_at < now() - r.threshold * interval '1 hour' AND f.doc_volume > 0
          AND NOT EXISTS (SELECT 1 FROM fuel_costs c WHERE c.station_id = f.station_id AND c.receipt_id = f.receipt_id)
        ON CONFLICT DO NOTHING
        """,
        // Fuel delivery: less received than on the document (or a negative delivery).
        Insert + """
        SELECT f.station_id, 'receipt_discrepancy', f.receipt_id::text, r.severity,
               CASE WHEN f.doc_volume < 0 THEN 'Отрицательный приём топлива: ' || fmt_num(f.doc_volume) || ' л'
                    ELSE 'Недостача при приёме: ' || fmt_num(f.doc_volume - COALESCE(f.measured_volume, f.doc_volume + f.discrepancy)) || ' л' END,
               format('Документ %s, машина %s: по документу %s л, принято %s л.', f.doc_number, f.car_number, fmt_num(f.doc_volume), fmt_num(COALESCE(f.measured_volume, f.doc_volume + f.discrepancy))),
               round(f.doc_volume - COALESCE(f.measured_volume, f.doc_volume + f.discrepancy), 2), f.session_id, f.received_at
        FROM fuel_receipts f
        JOIN v_station_rules r ON r.code = 'receipt_discrepancy' AND r.enabled AND r.station_id = f.station_id
        WHERE f.received_at > now() - interval '30 days'
          AND (f.doc_volume < 0 OR (f.doc_volume > 0 AND 100 * (f.doc_volume - COALESCE(f.measured_volume, f.doc_volume + f.discrepancy)) / f.doc_volume > r.threshold))
        ON CONFLICT DO NOTHING
        """,
        // A shift still open after the threshold (hours).
        Insert + """
        SELECT s.station_id, 'shift_too_long', s.session_id::text, r.severity,
               'Смена №' || s.session_num || ' открыта ' || round(extract(epoch FROM now() - s.started_at) / 3600) || ' ч',
               'Оператор ' || s.user_name || '. Проверьте, закрыта ли смена в Топазе.', round(extract(epoch FROM now() - s.started_at) / 3600, 1), s.session_id, s.started_at
        FROM shifts s JOIN v_station_rules r ON r.code = 'shift_too_long' AND r.enabled AND r.station_id = s.station_id
        WHERE s.ended_at IS NULL AND s.started_at < now() - make_interval(hours => r.threshold::int)
        ON CONFLICT DO NOTHING
        """,
        // Goods sold in the last 28 days but out of stock now (one alert per item per day).
        Insert + """
        SELECT m.station_id, 'goods_out', m.item_id || '/' || current_date, r.severity, 'Закончился товар: ' || COALESCE(k.name, m.item_id::text),
               'Продано за 28 дней: ' || fmt_num(s.sold) || ' шт.', s.sold, NULL, now()
        FROM (SELECT station_id, item_id, sum(quantity) AS stock FROM item_moves GROUP BY 1, 2) m
        JOIN (SELECT station_id, item_id, sum(quantity) AS sold FROM agg_shop_daily WHERE local_date >= current_date - 28 GROUP BY 1, 2) s
          ON s.station_id = m.station_id AND s.item_id = m.item_id
        LEFT JOIN v_item_keys k ON k.station_id = m.station_id AND k.item_id = m.item_id
        JOIN v_station_rules r ON r.code = 'goods_out' AND r.enabled AND r.station_id = m.station_id
        WHERE m.stock <= 0 AND s.sold > 0
        ON CONFLICT DO NOTHING
        """,
        // Station silent longer than the threshold (minutes): one alert per outage.
        Insert + """
        SELECT v.id, 'station_offline', 'since ' || COALESCE(v.last_seen_at::text, 'never'), r.severity,
               'АЗС не на связи ' || COALESCE(round(extract(epoch FROM v.silent_for) / 60) || ' мин', '(ни разу)'),
               'Последняя связь: ' || COALESCE(to_char(v.last_seen_at AT TIME ZONE 'Europe/Moscow', 'DD.MM.YYYY HH24:MI') || ' МСК', 'нет') || '. Данные копятся на АЗС и придут после восстановления связи.',
               round(extract(epoch FROM v.silent_for) / 60), NULL, v.last_seen_at
        FROM v_station_status v JOIN v_station_rules r ON r.code = 'station_offline' AND r.enabled AND r.station_id = v.id
        WHERE v.status = 'active' AND v.silent_for > make_interval(mins => r.threshold::int)
        ON CONFLICT DO NOTHING
        """,
        // Little free space on the Topaz DB drive or the system drive (one alert per day; closes when space is back).
        Insert + """
        SELECT s.id, 'disk_low', current_date::text, r.severity,
               'Мало места на диске: свободно ' || fmt_num(round(least(COALESCE(d.db_disk_free, d.sys_disk_free), COALESCE(d.sys_disk_free, d.db_disk_free)) / 1073741824.0, 1)) || ' ГБ',
               concat_ws(' ',
                 CASE WHEN d.db_disk_free IS NOT NULL THEN 'Диск с базой Топаза: свободно ' || fmt_num(round(d.db_disk_free / 1073741824.0, 1)) || ' из ' || fmt_num(round(d.db_disk_total / 1073741824.0)) || ' ГБ.' END,
                 CASE WHEN d.sys_disk_free IS NOT NULL THEN 'Системный диск: свободно ' || fmt_num(round(d.sys_disk_free / 1073741824.0, 1)) || ' из ' || fmt_num(round(d.sys_disk_total / 1073741824.0)) || ' ГБ.' END,
                 CASE WHEN d.db_size IS NOT NULL THEN 'База Топаза ' || fmt_num(round(d.db_size / 1048576.0)) || ' МБ'
                      || COALESCE(', растёт на ' || fmt_num(round(d.db_growth_per_day * 7 / 1048576.0)) || ' МБ в неделю', '') || '.' END,
                 CASE WHEN d.free_drop_per_day > 0 THEN 'При нынешнем темпе место закончится примерно через '
                      || ceil(least(COALESCE(d.db_disk_free, d.sys_disk_free), COALESCE(d.sys_disk_free, d.db_disk_free)) / d.free_drop_per_day) || ' дн.' END,
                 'Освободите место: старые резервные копии и журналы Топаза, временные файлы Windows.'),
               round(least(COALESCE(d.db_disk_free, d.sys_disk_free), COALESCE(d.sys_disk_free, d.db_disk_free)) / 1073741824.0, 2), NULL, now()
        FROM stations s JOIN v_station_disk d ON d.station_id = s.id
        JOIN v_station_rules r ON r.code = 'disk_low' AND r.enabled AND r.station_id = s.id
        WHERE s.status = 'active' AND s.host_checked_at > now() - interval '1 day'
          AND least(COALESCE(d.db_disk_free, d.sys_disk_free), COALESCE(d.sys_disk_free, d.db_disk_free)) < r.threshold * 1073741824
        ON CONFLICT DO NOTHING
        """,
        // PC clock (with the Windows time zone) differs from the true station time (one alert per day).
        Insert + """
        SELECT s.id, 'clock_skew', current_date::text, r.severity,
               'Часы ПК АЗС ' || CASE WHEN s.clock_skew_seconds > 0 THEN 'спешат' ELSE 'отстают' END || ' на ' || fmt_num(round(abs(s.clock_skew_seconds) / 60.0)) || ' мин',
               'Время на ПК: ' || COALESCE(s.last_heartbeat->'host'->>'local_clock', '?') || ', часовой пояс Windows: ' || COALESCE(s.last_heartbeat->'host'->>'windows_time_zone', '?')
                   || ', пояс АЗС на сервере: ' || s.time_zone || '. Топаз записывает время операций по этим часам — исправьте время или пояс на ПК.',
               round(s.clock_skew_seconds / 60.0, 1), NULL, now()
        FROM stations s JOIN v_station_rules r ON r.code = 'clock_skew' AND r.enabled AND r.station_id = s.id
        WHERE s.status = 'active' AND s.host_checked_at > now() - interval '1 day' AND abs(s.clock_skew_seconds) > r.threshold * 60
        ON CONFLICT DO NOTHING
        """,
        // A new agent asks to be connected (no station yet: owners and superadmins see it).
        """
        INSERT INTO alerts (station_id, registration_id, rule_code, dedupe_key, severity, title, details, value, session_id, occurred_at)
        SELECT NULL, g.id, 'station_registration', g.id::text, r.severity,
               'АЗС ожидает подключения: «' || g.station_name || '»',
               concat_ws(', ', 'ПК ' || g.machine_name, 'IP ' || g.ip, 'агент ' || g.agent_version)
                   || CASE WHEN p.id IS NOT NULL THEN '. Повторное подключение АЗС «' || COALESCE(NULLIF(p.display_name, ''), p.name) || '»' ELSE '' END
                   || '. Одобрите в «АЗС → Подключение», сверив код подтверждения с тем, кто устанавливал агент.',
               NULL, NULL, g.created_at
        FROM station_registrations g
        LEFT JOIN stations p ON p.id = g.previous_station_id
        JOIN alert_rules r ON r.code = 'station_registration' AND r.enabled
        WHERE g.status = 'pending'
        ON CONFLICT DO NOTHING
        """,
        // Station fuels without a confirmed reference type (one alert per set of such fuels; closes automatically).
        Insert + """
        SELECT f.station_id, 'fuel_unmapped', string_agg(f.fuel_id::text, ',' ORDER BY f.fuel_id), r.severity,
               'Топливо не сопоставлено со справочником: ' || count(*) || ' шт.',
               'Подтвердите вид в «Настройки → Виды топлива»: ' || string_agg(f.fuel_name || ' → ' || ft.name, ', ' ORDER BY f.fuel_name)
                   || '. Пока отчёты используют предложенный вид.',
               count(*), NULL, now()
        FROM v_station_fuels f
        JOIN fuel_types ft ON ft.code = f.suggested_type
        JOIN stations s ON s.id = f.station_id AND s.status = 'active'
        JOIN v_station_rules r ON r.code = 'fuel_unmapped' AND r.enabled AND r.station_id = f.station_id
        WHERE f.needs_confirmation
        GROUP BY f.station_id, r.severity
        ON CONFLICT DO NOTHING
        """,
        // Agent self-update failed (download, signature or installation; the agent rolled back). One alert per version.
        Insert + """
        SELECT u.station_id, 'agent_update_failed', u.version, r.severity,
               'Обновление агента до ' || u.version || ' не удалось',
               'Причина: ' || COALESCE(u.error, 'не указана') || '. Агент работает на прежней версии ' || COALESCE(s.agent_version, '?')
                   || '; повторит попытку не раньше чем через сутки (всего до 3 раз).',
               NULL, NULL, u.at
        FROM agent_update_reports u
        JOIN stations s ON s.id = u.station_id
        JOIN v_station_rules r ON r.code = 'agent_update_failed' AND r.enabled AND r.station_id = u.station_id
        WHERE u.status = 'failed' AND u.at > now() - interval '30 days'
        ON CONFLICT DO NOTHING
        """,
        // Rejected batches on the agent or failed processing on the server (one alert per day).
        Insert + """
        SELECT v.id, 'data_rejected', current_date::text, r.severity, 'Данные АЗС не принимаются',
               format('Отклонено агентом: %s, не обработано сервером: %s. См. «АЗС» и /api/v1/admin/batches.', COALESCE(v.agent_queue_dead, 0), v.server_failed),
               COALESCE(v.agent_queue_dead, 0) + v.server_failed, NULL, now()
        FROM v_station_status v JOIN v_station_rules r ON r.code = 'data_rejected' AND r.enabled AND r.station_id = v.id
        WHERE COALESCE(v.agent_queue_dead, 0) > 0 OR v.server_failed > 0
        ON CONFLICT DO NOTHING
        """,
    ];

    private static async Task<int> LowFuelAsync(NpgsqlConnection c, CancellationToken ct)
    {
        // Stations whose organization keeps the rule on.
        var stations = (await c.QueryAsync<Guid>(
            "SELECT s.id FROM stations s JOIN v_station_rules r ON r.station_id = s.id AND r.code = 'low_fuel' AND r.enabled WHERE s.status = 'active'")).ToArray();
        if (stations.Length == 0) return 0;
        var n = 0;
        foreach (var t in (await TankPlanner.ComputeAsync(c, stations)).Where(t => t.Status == "critical"))
        {
            n += await c.ExecuteAsync(new CommandDefinition(Insert + """
                SELECT @StationId, 'low_fuel', @key, r.severity, @title, @details, @value, @SessionId, now()
                FROM v_station_rules r WHERE r.code = 'low_fuel' AND r.station_id = @StationId
                ON CONFLICT DO NOTHING
                """, new
            {
                t.StationId, t.SessionId, key = $"{t.TankId}/{DateTime.UtcNow:yyyy-MM-dd}",
                title = $"Заканчивается {t.FuelName} (резервуар {t.TankNum}): {Ru(t.BookVolume, "#,0")} л",
                details = t.DaysToMin is { } d
                    ? $"Хватит примерно на {Ru(d, "0.#")} дн. при расходе {Ru(t.PerDay, "#,0")} л/день. Рекомендуемый заказ: {Ru(t.RecommendedOrder, "#,0")} л."
                    : $"Остаток ниже минимума ({Ru(t.Threshold, "#,0")} л).",
                value = t.DaysToMin,
            }, cancellationToken: ct));
        }
        return n;
    }

    /// <summary>Russian number format without culture data (invariant globalization): 15 630, 2,9.</summary>
    private static string Ru(decimal v, string format) =>
        v.ToString(format, CultureInfo.InvariantCulture).Replace(",", "\u00a0").Replace('.', ',').Replace("\u00a0", " ");

    // ---------------------------------------------------------------- notifications

    private sealed record Channel(long Id, string Target, string MinSeverity, bool DailyDigest, DateTime? LastDigest, Guid? OrganizationId);
    private sealed record Pending(long Id, string Station, string Severity, string Title, string? Details, DateTime? OccurredAt, string RuleCode, Guid? OrganizationId);

    /// <summary>Platform events (connection requests, agent updates) go to the superadmin's chats only.</summary>
    private static bool IsPlatform(Pending p) => p.OrganizationId is null || p.RuleCode is "agent_update_failed" or "station_registration";

    /// <summary>A chat of an organization hears only about its stations; a platform chat (no organization) about platform events.</summary>
    private static bool Routes(Channel ch, Pending p) => ch.OrganizationId is { } org ? !IsPlatform(p) && p.OrganizationId == org : IsPlatform(p);

    private async Task NotifyAsync(NpgsqlConnection c, CancellationToken ct)
    {
        var token = options.Value.TelegramBotToken;
        var pending = (await c.QueryAsync<Pending>("""
            SELECT a.id, COALESCE(sn.station, 'Новая АЗС') AS station, a.severity, a.title, a.details, a.occurred_at AS OccurredAt,
                   a.rule_code AS RuleCode, st.organization_id AS OrganizationId
            FROM alerts a LEFT JOIN v_station_names sn ON sn.station_id = a.station_id LEFT JOIN stations st ON st.id = a.station_id
            WHERE a.notified_at IS NULL ORDER BY a.id
            """)).ToList();
        var channels = (await c.QueryAsync<Channel>(
            "SELECT id, target, min_severity AS MinSeverity, daily_digest AS DailyDigest, last_digest AS LastDigest, organization_id AS OrganizationId FROM notification_channels WHERE enabled")).ToList();

        var canSend = !string.IsNullOrWhiteSpace(token) && channels.Count > 0;
        if (canSend)
        {
            // Only fresh events go out immediately; history found on the first run is not replayed.
            var fresh = pending.Where(p => p.OccurredAt is null || p.OccurredAt > DateTime.UtcNow.AddDays(-1)).ToList();
            await DigestAsync(c, token!, channels, ct);
            foreach (var ch in channels)
            {
                var mine = fresh.Where(p => Routes(ch, p) && SeverityRank[p.Severity] >= SeverityRank[ch.MinSeverity]).ToList();
                if (mine.Count > 0) await SendAsync(token!, ch.Target, Format(mine), ct);
            }
        }
        // Without a bot token or channels, fresh alerts wait (a channel added later still gets the last day);
        // older ones are marked so the backlog does not grow.
        var done = canSend ? pending : pending.Where(p => p.OccurredAt is { } at && at < DateTime.UtcNow.AddDays(-1)).ToList();
        if (done.Count > 0)
            await c.ExecuteAsync("UPDATE alerts SET notified_at = now() WHERE id = ANY(@ids)", new { ids = done.Select(p => p.Id).ToArray() });
    }

    private static string Format(IReadOnlyList<Pending> alerts)
    {
        var icon = new Dictionary<string, string> { ["critical"] = "🔴", ["warning"] = "🟠", ["info"] = "🔵" };
        var sb = new StringBuilder("<b>Topaz Center: новые события</b>\n");
        foreach (var a in alerts.OrderByDescending(a => SeverityRank[a.Severity]).Take(15))
            sb.Append('\n').Append(icon[a.Severity]).Append(" <b>").Append(WebUtility.HtmlEncode(a.Station)).Append("</b>: ")
              .Append(WebUtility.HtmlEncode(a.Title)).Append(a.Details is null ? "" : "\n<i>" + WebUtility.HtmlEncode(a.Details) + "</i>");
        if (alerts.Count > 15) sb.Append($"\n\n…и ещё {alerts.Count - 15}. Все события — в разделе «Контроль».");
        return sb.ToString();
    }

    /// <summary>Daily summary at 09:00 Moscow time: new alerts of the last 24 hours by severity.</summary>
    private async Task DigestAsync(NpgsqlConnection c, string token, List<Channel> channels, CancellationToken ct)
    {
        var msk = DateTime.UtcNow.AddHours(3);
        if (msk.Hour < 9) return;
        foreach (var ch in channels.Where(ch => ch.DailyDigest && (ch.LastDigest is null || ch.LastDigest.Value.Date < msk.Date)))
        {
            // The organization's own stations; a platform chat gets the whole picture (counts only, no data).
            var org = ch.OrganizationId;
            var counts = (await c.QueryAsync<(string Severity, long Count)>("""
                SELECT a.severity, count(*) FROM alerts a LEFT JOIN stations s ON s.id = a.station_id
                WHERE a.detected_at > now() - interval '24 hours' AND (@org::uuid IS NULL OR s.organization_id = @org) GROUP BY 1
                """, new { org })).ToDictionary(x => x.Severity, x => x.Count);
            var stations = await c.QuerySingleAsync<(long Total, long Silent)>("""
                SELECT count(*), count(*) FILTER (WHERE v.silent_for > interval '15 minutes' OR v.silent_for IS NULL)
                FROM v_station_status v JOIN stations s ON s.id = v.id WHERE v.status = 'active' AND (@org::uuid IS NULL OR s.organization_id = @org)
                """, new { org });
            var text = $"<b>Topaz Center: сводка за сутки</b>\nСобытий: 🔴 {counts.GetValueOrDefault("critical")} · 🟠 {counts.GetValueOrDefault("warning")} · 🔵 {counts.GetValueOrDefault("info")}\n" +
                       $"АЗС на связи: {stations.Total - stations.Silent} из {stations.Total}";
            if (await SendAsync(token, ch.Target, text, ct))
                await c.ExecuteAsync("UPDATE notification_channels SET last_digest = @d WHERE id = @id", new { d = msk.Date, id = ch.Id });
        }
    }

    public async Task<bool> SendAsync(string token, string chat, string html, CancellationToken ct)
    {
        try
        {
            var client = http.CreateClient("telegram");
            var url = $"{options.Value.TelegramApiBase.TrimEnd('/')}/bot{token}/sendMessage";
            var res = await client.PostAsJsonAsync(url, new Dictionary<string, object>
            {
                ["chat_id"] = chat, ["text"] = html, ["parse_mode"] = "HTML", ["disable_web_page_preview"] = true,
            }, ct);
            if (!res.IsSuccessStatusCode)
                log.LogWarning("Telegram send to {Chat} failed: {Status} {Body}", chat, (int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("Telegram send to {Chat} failed: {Error}", chat, ex.Message); // never log the token (it is in the URL)
            return false;
        }
    }
}
