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
public sealed class AnomalyDetector(NpgsqlDataSource db, TelegramApi telegram, ILogger<AnomalyDetector> log) : BackgroundService
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
            // Deletes of a compaction arrive over several batches: the open alert shows the current count, and closes
            // once the rows are back (the database restored from a copy).
            await c.ExecuteAsync("""
                UPDATE alerts a SET value = h.rows, title = 'Из базы АЗС удалены данные старых смен (записей: ' || fmt_num(h.rows) || ')'
                FROM v_held_delete_days h
                WHERE a.rule_code = 'history_deleted' AND a.status <> 'closed' AND h.station_id = a.station_id AND h.day::text = a.dedupe_key
                  AND a.value IS DISTINCT FROM h.rows
                """);
            await c.ExecuteAsync("""
                UPDATE alerts a SET status = 'closed', status_by = 'auto'
                WHERE a.rule_code = 'history_deleted' AND a.status <> 'closed'
                  AND NOT EXISTS (SELECT 1 FROM v_held_delete_days h WHERE h.station_id = a.station_id AND h.day::text = a.dedupe_key)
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
               'Прерванные наливы: ' || fmt_num(x.aborted) || ' из ' || fmt_num(x.total) || ' (' || fmt_num(round(100.0 * x.aborted / x.total)) || ' %)',
               format('Смена №%s, %s.', s.session_num, shift_operators_text(x.station_id, x.session_id, x.locals)), round(100.0 * x.aborted / x.total, 1), x.session_id, s.ended_at
        FROM (SELECT station_id, session_id, count(*) FILTER (WHERE volume = 0) AS aborted, count(*) AS total,
                     array_agg(DISTINCT local_session_id) FILTER (WHERE volume = 0) AS locals FROM fuel_sales GROUP BY 1, 2) x
        JOIN shifts s ON s.station_id = x.station_id AND s.session_id = x.session_id AND s.ended_at IS NOT NULL
        {string.Format(Rule, "aborted_share", "x")}
        WHERE x.aborted >= 5 AND 100.0 * x.aborted / x.total > r.threshold AND {Recent}
        ON CONFLICT DO NOTHING
        """,
        // Unpaid dispensing (tech pumping, overflow) in a closed shift.
        Insert + $"""
        SELECT x.station_id, 'service_volume', x.session_id::text, r.severity,
               'Отпуск без оплаты: ' || fmt_num(x.liters) || ' л за смену', format('Смена №%s, %s, операций: %s.', s.session_num, shift_operators_text(x.station_id, x.session_id, x.locals), fmt_num(x.ops)),
               x.liters, x.session_id, s.ended_at
        FROM (SELECT station_id, session_id, sum(volume) AS liters, count(*) AS ops, array_agg(DISTINCT local_session_id) AS locals
              FROM fuel_sales WHERE payment_kind = 'none' AND volume > 0 GROUP BY 1, 2) x
        JOIN shifts s ON s.station_id = x.station_id AND s.session_id = x.session_id AND s.ended_at IS NOT NULL
        {string.Format(Rule, "service_volume", "x")}
        WHERE x.liters > r.threshold AND {Recent}
        ON CONFLICT DO NOTHING
        """,
        // Discounts share of fuel revenue in a closed shift.
        Insert + $"""
        SELECT x.station_id, 'discount_share', x.session_id::text, r.severity,
               'Скидки ' || fmt_num(round(100 * x.discounts / x.revenue, 1)) || ' % выручки смены', format('Смена №%s, %s: скидки %s ₽.', s.session_num, shift_operators_text(x.station_id, x.session_id, x.locals), fmt_num(x.discounts)),
               round(100 * x.discounts / x.revenue, 2), x.session_id, s.ended_at
        FROM (SELECT station_id, session_id, sum(fuel_discount(amount_discount, price, price_pump, volume)) AS discounts, sum(amount) AS revenue,
                     array_agg(DISTINCT local_session_id) FILTER (WHERE fuel_discount(amount_discount, price, price_pump, volume) > 0) AS locals
              FROM fuel_sales GROUP BY 1, 2) x
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
               'Цена отпуска не сходится: ' || fmt_num(x.orders) || ' отпуск(ов)',
               format('Смена №%s, %s: %s. Например, отпуск №%s: %s л, цена заказа %s ₽, на колонке %s ₽, сумма %s ₽.', s.session_num,
                      shift_operators_text(x.station_id, x.session_id, x.locals),
                      concat_ws(', ', CASE WHEN x.overcharge > 0 THEN 'переплата клиента — ' || fmt_num(x.overcharge) END,
                                      CASE WHEN x.amount_off > 0 THEN 'сумма ≠ литры × цена колонки — ' || fmt_num(x.amount_off) END,
                                      CASE WHEN x.bypass > 0 THEN 'скидка мимо скидок Топаза — ' || fmt_num(x.bypass) END),
                      x.order_num, fmt_num(x.volume), fmt_num(x.price), fmt_num(x.price_pump), fmt_num(x.amount)),
               x.orders, x.session_id, s.started_at
        FROM (SELECT y.station_id, y.session_id, count(*) AS orders,
                     count(*) FILTER (WHERE y.overcharge) AS overcharge, count(*) FILTER (WHERE y.amount_off) AS amount_off,
                     count(*) FILTER (WHERE y.bypass) AS bypass,
                     (array_agg(y.order_num ORDER BY y.sold_at))[1] AS order_num, (array_agg(y.volume ORDER BY y.sold_at))[1] AS volume,
                     (array_agg(y.price ORDER BY y.sold_at))[1] AS price, (array_agg(y.price_pump ORDER BY y.sold_at))[1] AS price_pump,
                     (array_agg(y.amount ORDER BY y.sold_at))[1] AS amount, array_agg(DISTINCT y.local_session_id) AS locals
              FROM (SELECT f.station_id, f.session_id, f.local_session_id, f.order_num, f.sold_at, f.volume, f.price, f.price_pump, f.amount,
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
               format('Слив %s, резервуар %s, документ %s. Введите закупочную цену в «Деньги → Себестоимость» — пока её нет, слив считается по средней цене резервуара.',
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
        // Measured stock (level gauge, or entered by hand) at a shift close below the book by more than the threshold.
        Insert + $"""
        SELECT m.station_id, 'tank_shortage', m.tank_id || '/' || m.session_id, r.severity,
               'Недостача по замеру: резервуар ' || COALESCE(m.tank_num::text, m.tank_id::text) || ', ' || fmt_num(-m.diff) || ' л',
               format('Смена №%s, %s: по учёту Топаза %s л, по замеру %s л; отпущено за смену %s л.',
                      m.session_num, COALESCE(m.fuel_name, 'топливо'), fmt_num(m.book_volume), fmt_num(m.fact_volume), fmt_num(m.pump_volume)),
               round(-m.diff, 2), m.session_id, m.ended_at
        FROM v_tank_measurements m
        {string.Format(Rule, "tank_shortage", "m")}
        WHERE -m.diff > r.threshold AND m.started_at > now() - interval '30 days'
        ON CONFLICT DO NOTHING
        """,
        // Measured stock above the book by more than the threshold: usually a delivery not entered in Topaz as a receipt.
        Insert + $"""
        SELECT m.station_id, 'tank_surplus', m.tank_id || '/' || m.session_id, r.severity,
               'Излишек по замеру: резервуар ' || COALESCE(m.tank_num::text, m.tank_id::text) || ', ' || fmt_num(m.diff) || ' л',
               format('Смена №%s, %s: по учёту Топаза %s л, по замеру %s л. Проверьте, оформлен ли в Топазе слив.',
                      m.session_num, COALESCE(m.fuel_name, 'топливо'), fmt_num(m.book_volume), fmt_num(m.fact_volume)),
               round(m.diff, 2), m.session_id, m.ended_at
        FROM v_tank_measurements m
        {string.Format(Rule, "tank_surplus", "m")}
        WHERE m.diff > r.threshold AND m.started_at > now() - interval '30 days'
        ON CONFLICT DO NOTHING
        """,
        // Rows of old shifts vanished from Topaz (database compaction): the deletes were held, the history is kept.
        Insert + $"""
        SELECT h.station_id, 'history_deleted', h.day::text, r.severity,
               'Из базы АЗС удалены данные старых смен (записей: ' || fmt_num(h.rows) || ')',
               format('Смены №%s–%s (%s – %s). Обычно так бывает после свёртки базы Топаза. В сервисе история этих смен сохранена.',
                      h.num_from, h.num_to, to_char(h.from_at AT TIME ZONE sn.time_zone, 'DD.MM.YYYY'), to_char(h.to_at AT TIME ZONE sn.time_zone, 'DD.MM.YYYY')),
               h.rows, NULL, h.last_at
        FROM v_held_delete_days h
        JOIN v_station_names sn ON sn.station_id = h.station_id
        {string.Format(Rule, "history_deleted", "h")}
        ON CONFLICT DO NOTHING
        """,
        // Sales without a discount at a payment mode that has a namesake with a discount (Topaz shows both under one
        // name): the operator picks the old mode and the customer does not get the discount.
        Insert + $"""
        WITH m AS (
            SELECT pm.station_id, pm.payment_mode_id, pm.name, lower_ru(trim(pm.name)) AS k,
                   (SELECT min(d.sold_at) FROM fuel_sales d WHERE d.station_id = pm.station_id AND d.payment_mode_id = pm.payment_mode_id
                      AND d.volume > 0 AND d.sold_at > now() - interval '61 days'
                      AND fuel_discount(d.amount_discount, d.price, d.price_pump, d.volume) > 0) AS discount_from
            FROM payment_modes pm WHERE COALESCE(trim(pm.name), '') <> ''
        ), tw AS (
            -- discount_from: the namesake's first discounted sale; shifts that ended before it had no discount to miss.
            SELECT a.station_id, a.payment_mode_id, a.name, string_agg(b.payment_mode_id::text, ', ' ORDER BY b.payment_mode_id) AS twins,
                   min(b.discount_from) AS discount_from
            FROM m a JOIN m b ON b.station_id = a.station_id AND b.k = a.k AND b.payment_mode_id <> a.payment_mode_id AND b.discount_from IS NOT NULL
            WHERE a.discount_from IS NULL
            GROUP BY 1, 2, 3
        )
        SELECT x.station_id, 'payment_mode_twin', x.session_id || '/' || x.payment_mode_id, r.severity,
               'Скидка не применилась: «' || x.name || '», наливов без скидки: ' || fmt_num(x.orders),
               format('Смена №%s, оператор %s: %s налив(ов) на %s л по виду оплаты «%s» (код %s) без скидки. У вида оплаты с тем же названием (код %s) скидка есть. Проверьте, какой из них выбирает оператор, и отключите лишний в Топазе.',
                      s.session_num, s.user_name, fmt_num(x.orders), fmt_num(x.liters), x.name, x.payment_mode_id, x.twins),
               x.orders, x.session_id, s.ended_at
        FROM (SELECT f.station_id, f.session_id, f.payment_mode_id, tw.name, tw.twins, tw.discount_from, count(*) AS orders, sum(f.volume) AS liters
              FROM fuel_sales f
              JOIN tw ON tw.station_id = f.station_id AND tw.payment_mode_id = f.payment_mode_id
              WHERE f.volume > 0 AND f.sold_at > now() - interval '31 days'
              GROUP BY 1, 2, 3, 4, 5, 6) x
        JOIN shifts s ON s.station_id = x.station_id AND s.session_id = x.session_id AND s.ended_at IS NOT NULL AND s.ended_at >= x.discount_from
        {string.Format(Rule, "payment_mode_twin", "x")}
        WHERE {Recent}
        ON CONFLICT DO NOTHING
        """,
        // Many overflows (Topaz's service payment mode "Переливы") on one hose within 7 days: the pump needs a check.
        // One alert per hose and week.
        Insert + $"""
        SELECT x.station_id, 'hose_overflows', x.hose_id || '/' || to_char(now(), 'IYYY-IW'), r.severity,
               'Частые переливы: колонка ' || COALESCE(p.num::text, '?') || ', рукав ' || COALESCE(h.num_in_pump::text, x.hose_id::text) || ' — ' || fmt_num(x.ops) || ' раз за 7 дней',
               format('Переливы на %s л (отрицательных: %s). Частые переливы бывают из-за сброса заказов и перезагрузок, наливов с пульта, сдвига счётчиков, неверной дискретности или разного округления на ТРК, контроллере и в Топазе.',
                      fmt_num(x.liters), fmt_num(x.negative)),
               x.ops, x.last_session, x.last_at
        FROM (SELECT f.station_id, f.hose_id, count(*) AS ops, sum(f.volume) AS liters, count(*) FILTER (WHERE f.volume < 0) AS negative,
                     max(f.sold_at) AS last_at, (array_agg(f.session_id ORDER BY f.sold_at DESC))[1] AS last_session
              FROM fuel_sales f
              JOIN payment_modes pm ON pm.station_id = f.station_id AND pm.payment_mode_id = f.payment_mode_id
              WHERE lower_ru(COALESCE(pm.name, '')) ~ 'перелив' AND f.volume <> 0 AND f.hose_id IS NOT NULL
                AND f.sold_at > now() - interval '7 days'
              GROUP BY 1, 2) x
        LEFT JOIN v_hoses h ON h.station_id = x.station_id AND h.hose_id = x.hose_id
        LEFT JOIN v_pumps p ON p.station_id = h.station_id AND p.pump_id = h.pump_id
        {string.Format(Rule, "hose_overflows", "x")}
        WHERE x.ops >= r.threshold
        ON CONFLICT DO NOTHING
        """,
        // A shift still open after the threshold (hours).
        Insert + """
        SELECT s.station_id, 'shift_too_long', s.session_id::text, r.severity,
               'Смена №' || s.session_num || ' открыта ' || fmt_num(round(extract(epoch FROM now() - s.started_at) / 3600)) || ' ч',
               (SELECT upper(left(t, 1)) || substr(t, 2) FROM shift_operators_text(s.station_id, s.session_id,
                   (SELECT array_agg(w.local_session_id) FROM workplace_shifts w WHERE w.station_id = s.station_id AND w.session_id = s.session_id AND w.ended_at IS NULL)) t)
                 || '. Проверьте, закрыта ли смена в Топазе.', round(extract(epoch FROM now() - s.started_at) / 3600, 1), s.session_id, s.started_at
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
               'АЗС не на связи ' || COALESCE(fmt_duration(v.silent_for), '(ни разу)'),
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
                      || fmt_num(ceil(least(COALESCE(d.db_disk_free, d.sys_disk_free), COALESCE(d.sys_disk_free, d.db_disk_free)) / d.free_drop_per_day)) || ' дн.' END,
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
                   || '. Одобрите в «Платформа → Подключение АЗС», сверив код подтверждения с тем, кто устанавливал агент.',
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
               'Топливо не сопоставлено со справочником: ' || fmt_num(count(*)) || ' шт.',
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
               format('Отклонено агентом: %s, не обработано сервером: %s. См. «АЗС» и /api/v1/admin/batches.', fmt_num(COALESCE(v.agent_queue_dead, 0)), fmt_num(v.server_failed)),
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

    /// <summary>
    /// A notification chat. UserId set = a user's own chat: Stations are the stations this user can see
    /// (null for a superadmin: everything, platform events included).
    /// </summary>
    private sealed class Channel
    {
        public long Id { get; init; }
        public string Target { get; init; } = "";
        public string MinSeverity { get; init; } = "warning";
        public bool DailyDigest { get; init; }
        public DateTime? LastDigest { get; init; }
        public Guid? OrganizationId { get; init; }
        public Guid? UserId { get; init; }
        public Guid[]? Stations { get; init; } // Dapper cannot pass uuid[] to a record constructor
    }
    private sealed record Pending(long Id, string Station, string Severity, string Title, string? Details, DateTime? OccurredAt, string RuleCode, Guid? OrganizationId, Guid? StationId);

    /// <summary>Platform events (connection requests, agent updates) go to the superadmin's chats only.</summary>
    private static bool IsPlatform(Pending p) => p.OrganizationId is null || p.RuleCode is "agent_update_failed" or "station_registration";

    /// <summary>
    /// A chat of an organization hears only about its stations; a platform chat (no organization) about platform events;
    /// a user's own chat about the stations the user sees (a superadmin's: everything).
    /// </summary>
    private static bool Routes(Channel ch, Pending p)
    {
        if (ch.UserId is not null)
            return ch.Stations is null || (!IsPlatform(p) && p.StationId is { } s && ch.Stations.Contains(s));
        return ch.OrganizationId is { } org ? !IsPlatform(p) && p.OrganizationId == org : IsPlatform(p);
    }

    /// <summary>
    /// Enabled chats. A user's own chat is read with the user's current access (same rules as StationScope):
    /// a blocked user or a removed station stops the messages at the next round.
    /// </summary>
    private const string ChannelsSql = """
        SELECT ch.id, ch.target, ch.min_severity AS MinSeverity, ch.daily_digest AS DailyDigest, ch.last_digest AS LastDigest,
               ch.organization_id AS OrganizationId, ch.user_id AS UserId,
               CASE WHEN u.id IS NULL OR u.role = 'superadmin' THEN NULL
                    WHEN u.role = 'owner' THEN ARRAY(SELECT s.id FROM stations s WHERE s.organization_id = u.organization_id)
                    ELSE ARRAY(SELECT us.station_id FROM user_stations us JOIN stations s ON s.id = us.station_id
                               WHERE us.user_id = u.id AND s.organization_id = u.organization_id AND s.status <> 'revoked') END AS Stations
        FROM notification_channels ch LEFT JOIN users u ON u.id = ch.user_id
        WHERE ch.enabled AND (ch.user_id IS NULL OR u.is_active)
        """;

    private async Task NotifyAsync(NpgsqlConnection c, CancellationToken ct)
    {
        var pending = (await c.QueryAsync<Pending>("""
            SELECT a.id, COALESCE(sn.station, 'Новая АЗС') AS station, a.severity, a.title, a.details, a.occurred_at AS OccurredAt,
                   a.rule_code AS RuleCode, st.organization_id AS OrganizationId, a.station_id AS StationId
            FROM alerts a LEFT JOIN v_station_names sn ON sn.station_id = a.station_id LEFT JOIN stations st ON st.id = a.station_id
            WHERE a.notified_at IS NULL ORDER BY a.id
            """)).ToList();
        var channels = (await c.QueryAsync<Channel>(ChannelsSql)).ToList();

        var canSend = telegram.Configured && channels.Count > 0;
        if (canSend)
        {
            // Only fresh events go out immediately; history found on the first run is not replayed.
            var fresh = pending.Where(p => p.OccurredAt is null || p.OccurredAt > DateTime.UtcNow.AddDays(-1)).ToList();
            await DigestAsync(c, channels, ct);
            foreach (var ch in channels)
            {
                var mine = fresh.Where(p => Routes(ch, p) && SeverityRank[p.Severity] >= SeverityRank[ch.MinSeverity]).ToList();
                if (mine.Count > 0) await SendAsync(c, ch, Format(mine), ct);
            }
        }
        // Without a bot token or channels, fresh alerts wait (a channel added later still gets the last day);
        // older ones are marked so the backlog does not grow.
        var done = canSend ? pending : pending.Where(p => p.OccurredAt is { } at && at < DateTime.UtcNow.AddDays(-1)).ToList();
        if (done.Count > 0)
            await c.ExecuteAsync("UPDATE alerts SET notified_at = now() WHERE id = ANY(@ids)", new { ids = done.Select(p => p.Id).ToArray() });
    }

    /// <summary>
    /// Sends to a channel and keeps the channel list right: a group that became a supergroup gets its new id
    /// (and the message is resent there); a chat that blocked or removed the bot is switched off.
    /// </summary>
    private async Task<bool> SendAsync(NpgsqlConnection c, Channel ch, string html, CancellationToken ct)
    {
        var r = await telegram.SendAsync(ch.Target, html, ct);
        if (r.MigratedTo is { } to)
        {
            await c.ExecuteAsync("UPDATE notification_channels SET target = @to WHERE id = @id", new { to, id = ch.Id });
            r = await telegram.SendAsync(to, html, ct);
        }
        if (r.Gone)
        {
            await c.ExecuteAsync("UPDATE notification_channels SET enabled = false WHERE id = @id", new { id = ch.Id });
            log.LogInformation("Telegram channel {Id} switched off: the bot was blocked or removed from the chat", ch.Id);
        }
        return r.Ok;
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
    private async Task DigestAsync(NpgsqlConnection c, List<Channel> channels, CancellationToken ct)
    {
        var msk = DateTime.UtcNow.AddHours(3);
        if (msk.Hour < 9) return;
        foreach (var ch in channels.Where(ch => ch.DailyDigest && (ch.LastDigest is null || ch.LastDigest.Value.Date < msk.Date)))
        {
            // The organization's own stations (a user's own chat: the user's stations); a platform chat or a
            // superadmin's own chat gets the whole picture (counts only, no data).
            var org = ch.UserId is not null && ch.Stations is null ? null : ch.OrganizationId;
            var ids = ch.Stations;
            var counts = (await c.QueryAsync<(string Severity, long Count)>("""
                SELECT a.severity, count(*) FROM alerts a LEFT JOIN stations s ON s.id = a.station_id
                WHERE a.detected_at > now() - interval '24 hours' AND (@org::uuid IS NULL OR s.organization_id = @org)
                  AND (@ids::uuid[] IS NULL OR a.station_id = ANY(@ids)) GROUP BY 1
                """, new { org, ids })).ToDictionary(x => x.Severity, x => x.Count);
            var stations = await c.QuerySingleAsync<(long Total, long Silent)>("""
                SELECT count(*), count(*) FILTER (WHERE v.silent_for > interval '15 minutes' OR v.silent_for IS NULL)
                FROM v_station_status v JOIN stations s ON s.id = v.id WHERE v.status = 'active' AND (@org::uuid IS NULL OR s.organization_id = @org)
                  AND (@ids::uuid[] IS NULL OR s.id = ANY(@ids))
                """, new { org, ids });
            var text = $"<b>Topaz Center: сводка за сутки</b>\nСобытий: 🔴 {counts.GetValueOrDefault("critical")} · 🟠 {counts.GetValueOrDefault("warning")} · 🔵 {counts.GetValueOrDefault("info")}\n" +
                       $"АЗС на связи: {stations.Total - stations.Silent} из {stations.Total}";
            if (await SendAsync(c, ch, text, ct))
                await c.ExecuteAsync("UPDATE notification_channels SET last_digest = @d WHERE id = @id", new { d = msk.Date, id = ch.Id });
        }
    }
}
