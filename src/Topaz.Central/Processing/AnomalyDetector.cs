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
                FROM stations s, v_station_disk d, alert_rules r
                WHERE a.station_id = s.id AND d.station_id = s.id AND r.code = a.rule_code AND a.status <> 'closed'
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
    private const string Rule = "JOIN alert_rules r ON r.code = '{0}' AND r.enabled";
    private const string Recent = "s.started_at > now() - interval '30 days'";

    private static readonly string[] RuleSql =
    [
        // Counter of a hose at the start of a shift is above its end in the previous shift: fuel dispensed between shifts.
        Insert + $"""
        SELECT h.station_id, 'counter_gap', h.hose_id || '/' || h.session_id, r.severity,
               'Разрыв счётчика рукава ' || h.hose_id || ': ' || fmt_num(h.start_counter - h.prev_end) || ' л',
               format('Смена №%s: на начало смены %s, на конец предыдущей %s. Возможен отпуск мимо учёта.', s.session_num, fmt_num(h.start_counter), fmt_num(h.prev_end)),
               round(h.start_counter - h.prev_end, 2), h.session_id, s.started_at
        FROM (SELECT station_id, hose_id, session_id, start_counter,
                     lag(end_counter) OVER (PARTITION BY station_id, hose_id ORDER BY session_id) AS prev_end
              FROM hose_shift_counters) h
        JOIN shifts s ON s.station_id = h.station_id AND s.session_id = h.session_id
        {string.Format(Rule, "counter_gap")}
        WHERE h.start_counter - h.prev_end > r.threshold AND {Recent}
        ON CONFLICT DO NOTHING
        """,
        // Counter delta of a closed shift differs from the orders of that hose.
        Insert + $"""
        SELECT h.station_id, 'counter_mismatch', h.hose_id || '/' || h.session_id, r.severity,
               'Счётчик рукава ' || h.hose_id || ' расходится с заказами на ' || fmt_num(abs(h.end_counter - h.start_counter - COALESCE(o.liters, 0))) || ' л',
               format('Смена №%s: по счётчику %s л, по заказам %s л.', s.session_num, fmt_num(h.end_counter - h.start_counter), fmt_num(COALESCE(o.liters, 0))),
               round(h.end_counter - h.start_counter - COALESCE(o.liters, 0), 2), h.session_id, s.ended_at
        FROM hose_shift_counters h
        JOIN shifts s ON s.station_id = h.station_id AND s.session_id = h.session_id AND s.ended_at IS NOT NULL
        LEFT JOIN (SELECT station_id, session_id, hose_id, sum(volume) AS liters FROM fuel_sales GROUP BY 1, 2, 3) o
               ON o.station_id = h.station_id AND o.session_id = h.session_id AND o.hose_id = h.hose_id
        {string.Format(Rule, "counter_mismatch")}
        WHERE abs(h.end_counter - h.start_counter - COALESCE(o.liters, 0)) > r.threshold AND {Recent}
        ON CONFLICT DO NOTHING
        """,
        // Aborted fuellings (volume 0) share of a closed shift.
        Insert + $"""
        SELECT x.station_id, 'aborted_share', x.session_id::text, r.severity,
               'Прерванные наливы: ' || x.aborted || ' из ' || x.total || ' (' || round(100.0 * x.aborted / x.total) || '%)',
               format('Смена №%s, оператор %s.', s.session_num, s.user_name), round(100.0 * x.aborted / x.total, 1), x.session_id, s.ended_at
        FROM (SELECT station_id, session_id, count(*) FILTER (WHERE volume = 0) AS aborted, count(*) AS total FROM fuel_sales GROUP BY 1, 2) x
        JOIN shifts s ON s.station_id = x.station_id AND s.session_id = x.session_id AND s.ended_at IS NOT NULL
        {string.Format(Rule, "aborted_share")}
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
        {string.Format(Rule, "service_volume")}
        WHERE x.liters > r.threshold AND {Recent}
        ON CONFLICT DO NOTHING
        """,
        // Discounts share of fuel revenue in a closed shift.
        Insert + $"""
        SELECT x.station_id, 'discount_share', x.session_id::text, r.severity,
               'Скидки ' || fmt_num(round(100 * x.discounts / x.revenue, 1)) || '% выручки смены', format('Смена №%s, оператор %s: скидки %s ₽.', s.session_num, s.user_name, fmt_num(x.discounts)),
               round(100 * x.discounts / x.revenue, 2), x.session_id, s.ended_at
        FROM (SELECT station_id, session_id, sum(amount_discount) AS discounts, sum(amount) AS revenue FROM fuel_sales GROUP BY 1, 2) x
        JOIN shifts s ON s.station_id = x.station_id AND s.session_id = x.session_id AND s.ended_at IS NOT NULL
        {string.Format(Rule, "discount_share")}
        WHERE x.revenue > 0 AND 100 * x.discounts / x.revenue > r.threshold AND {Recent}
        ON CONFLICT DO NOTHING
        """,
        // Order price differs from the pump price.
        Insert + $"""
        SELECT x.station_id, 'price_mismatch', x.session_id::text, r.severity,
               'Цена заказа ≠ цене ТРК: ' || x.orders || ' отпуск(ов)', format('Смена №%s: например, %s ₽ при цене на колонке %s ₽.', s.session_num, fmt_num(x.price), fmt_num(x.price_pump)),
               x.orders, x.session_id, s.started_at
        FROM (SELECT station_id, session_id, count(*) AS orders, min(price) AS price, min(price_pump) AS price_pump
              FROM fuel_sales f, alert_rules rr WHERE rr.code = 'price_mismatch' AND f.price_pump > 0 AND f.volume > 0 AND abs(f.price - f.price_pump) > rr.threshold
              GROUP BY 1, 2) x
        JOIN shifts s ON s.station_id = x.station_id AND s.session_id = x.session_id
        {string.Format(Rule, "price_mismatch")}
        WHERE {Recent}
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
        JOIN alert_rules r ON r.code = 'receipt_discrepancy' AND r.enabled
        WHERE f.received_at > now() - interval '30 days'
          AND (f.doc_volume < 0 OR (f.doc_volume > 0 AND 100 * (f.doc_volume - COALESCE(f.measured_volume, f.doc_volume + f.discrepancy)) / f.doc_volume > r.threshold))
        ON CONFLICT DO NOTHING
        """,
        // A shift still open after the threshold (hours).
        Insert + """
        SELECT s.station_id, 'shift_too_long', s.session_id::text, r.severity,
               'Смена №' || s.session_num || ' открыта ' || round(extract(epoch FROM now() - s.started_at) / 3600) || ' ч',
               'Оператор ' || s.user_name || '. Проверьте, закрыта ли смена в Топазе.', round(extract(epoch FROM now() - s.started_at) / 3600, 1), s.session_id, s.started_at
        FROM shifts s JOIN alert_rules r ON r.code = 'shift_too_long' AND r.enabled
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
        JOIN alert_rules r ON r.code = 'goods_out' AND r.enabled
        WHERE m.stock <= 0 AND s.sold > 0
        ON CONFLICT DO NOTHING
        """,
        // Station silent longer than the threshold (minutes): one alert per outage.
        Insert + """
        SELECT v.id, 'station_offline', 'since ' || COALESCE(v.last_seen_at::text, 'never'), r.severity,
               'АЗС не на связи ' || COALESCE(round(extract(epoch FROM v.silent_for) / 60) || ' мин', '(ни разу)'),
               'Последняя связь: ' || COALESCE(to_char(v.last_seen_at AT TIME ZONE 'Europe/Moscow', 'DD.MM.YYYY HH24:MI') || ' МСК', 'нет') || '. Данные копятся на АЗС и придут после восстановления связи.',
               round(extract(epoch FROM v.silent_for) / 60), NULL, v.last_seen_at
        FROM v_station_status v JOIN alert_rules r ON r.code = 'station_offline' AND r.enabled
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
        JOIN alert_rules r ON r.code = 'disk_low' AND r.enabled
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
        FROM stations s JOIN alert_rules r ON r.code = 'clock_skew' AND r.enabled
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
        JOIN alert_rules r ON r.code = 'fuel_unmapped' AND r.enabled
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
        JOIN alert_rules r ON r.code = 'agent_update_failed' AND r.enabled
        WHERE u.status = 'failed' AND u.at > now() - interval '30 days'
        ON CONFLICT DO NOTHING
        """,
        // Rejected batches on the agent or failed processing on the server (one alert per day).
        Insert + """
        SELECT v.id, 'data_rejected', current_date::text, r.severity, 'Данные АЗС не принимаются',
               format('Отклонено агентом: %s, не обработано сервером: %s. См. «АЗС» и /api/v1/admin/batches.', COALESCE(v.agent_queue_dead, 0), v.server_failed),
               COALESCE(v.agent_queue_dead, 0) + v.server_failed, NULL, now()
        FROM v_station_status v JOIN alert_rules r ON r.code = 'data_rejected' AND r.enabled
        WHERE COALESCE(v.agent_queue_dead, 0) > 0 OR v.server_failed > 0
        ON CONFLICT DO NOTHING
        """,
    ];

    private static async Task<int> LowFuelAsync(NpgsqlConnection c, CancellationToken ct)
    {
        if (!await c.ExecuteScalarAsync<bool>("SELECT enabled FROM alert_rules WHERE code = 'low_fuel'")) return 0;
        var stations = (await c.QueryAsync<Guid>("SELECT id FROM stations WHERE status = 'active'")).ToArray();
        var n = 0;
        foreach (var t in (await TankPlanner.ComputeAsync(c, stations)).Where(t => t.Status == "critical"))
        {
            n += await c.ExecuteAsync(new CommandDefinition(Insert + """
                SELECT @StationId, 'low_fuel', @key, r.severity, @title, @details, @value, @SessionId, now()
                FROM alert_rules r WHERE r.code = 'low_fuel'
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

    private sealed record Channel(long Id, string Target, string MinSeverity, bool DailyDigest, DateTime? LastDigest);
    private sealed record Pending(long Id, string Station, string Severity, string Title, string? Details, DateTime? OccurredAt);

    private async Task NotifyAsync(NpgsqlConnection c, CancellationToken ct)
    {
        var token = options.Value.TelegramBotToken;
        var pending = (await c.QueryAsync<Pending>("""
            SELECT a.id, COALESCE(sn.station, 'Новая АЗС') AS station, a.severity, a.title, a.details, a.occurred_at AS OccurredAt
            FROM alerts a LEFT JOIN v_station_names sn ON sn.station_id = a.station_id
            WHERE a.notified_at IS NULL ORDER BY a.id
            """)).ToList();
        var channels = (await c.QueryAsync<Channel>(
            "SELECT id, target, min_severity AS MinSeverity, daily_digest AS DailyDigest, last_digest AS LastDigest FROM notification_channels WHERE enabled")).ToList();

        var canSend = !string.IsNullOrWhiteSpace(token) && channels.Count > 0;
        if (canSend)
        {
            // Only fresh events go out immediately; history found on the first run is not replayed.
            var fresh = pending.Where(p => p.OccurredAt is null || p.OccurredAt > DateTime.UtcNow.AddDays(-1)).ToList();
            await DigestAsync(c, token!, channels, ct);
            foreach (var ch in channels)
            {
                var mine = fresh.Where(p => SeverityRank[p.Severity] >= SeverityRank[ch.MinSeverity]).ToList();
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
            var counts = (await c.QueryAsync<(string Severity, long Count)>(
                "SELECT severity, count(*) FROM alerts WHERE detected_at > now() - interval '24 hours' GROUP BY 1")).ToDictionary(x => x.Severity, x => x.Count);
            var stations = await c.QuerySingleAsync<(long Total, long Silent)>(
                "SELECT count(*), count(*) FILTER (WHERE silent_for > interval '15 minutes' OR silent_for IS NULL) FROM v_station_status WHERE status = 'active'");
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
