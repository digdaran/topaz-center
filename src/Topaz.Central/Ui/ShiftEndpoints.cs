using Dapper;
using Npgsql;

namespace Topaz.Central.Ui;

public static class ShiftEndpoints
{
    public static void MapUiShifts(this RouteGroupBuilder ui)
    {
        ui.MapGet("/shifts", List);
        ui.MapGet("/shifts/operators", Operators);
        ui.MapGet("/shifts/{station:guid}/{session:long}", Card);
    }

    private const string ShiftWhere = """
        t.station_id = ANY(@st) AND t.started_at >= @utcFrom AND t.started_at < @utcTo
        AND (t.started_at AT TIME ZONE sn.time_zone)::date BETWEEN @from::date AND @to::date
        """;

    /// <summary>Fuel revenue of a shift by payment category (the Topaz-like totals keep Topaz's own cash/cashless rules).</summary>
    private const string PayByShift = """
        LEFT JOIN LATERAL (
            SELECT sum(v.amount) FILTER (WHERE v.pay_category = 'cash') AS pay_cash, sum(v.amount) FILTER (WHERE v.pay_category = 'card') AS pay_card,
                   sum(v.amount) FILTER (WHERE v.pay_category = 'contract') AS pay_contract, sum(v.amount) FILTER (WHERE v.pay_category = 'fuel_card') AS pay_fuel_card
            FROM v_fuel_sales_local v WHERE v.station_id = t.station_id AND v.session_id = t.session_id) p ON true
        """;

    /// <summary>
    /// Operators of a shift (1.11): one per workplace (main first), e.g. "Инна, Оля" when a second operator worked at the
    /// additional workplace; the shift's UserName when Topaz has no local shifts. workplaces = workplaces opened in the shift.
    /// </summary>
    private const string OperatorsByShift = """
        LEFT JOIN LATERAL (
            SELECT string_agg(x.operator, ', ' ORDER BY x.main DESC, x.first_at) AS operators, max(x.places) AS workplaces
            FROM (SELECT w.operator, bool_or(w.is_main) AS main, min(w.started_at) AS first_at,
                         (SELECT count(DISTINCT y.workbench_id) FROM workplace_shifts y WHERE y.station_id = t.station_id AND y.session_id = t.session_id) AS places
                  FROM v_workplace_shifts w WHERE w.station_id = t.station_id AND w.session_id = t.session_id
                  GROUP BY w.operator) x) wo ON true
        """;

    private static async Task<IResult> List(HttpContext ctx, StationScope scope, UiDb db)
    {
        var (f, error) = await UiFilter.ParseAsync(ctx, scope);
        if (error is not null) return error;
        await using var c = await db.OpenConnectionAsync(ctx.RequestAborted);
        var rows = await c.QueryAsync($"""
            SELECT t.station_id, sn.station, t.session_id, t.session_num, t.user_name, t.started_at, t.ended_at,
                   COALESCE(wo.operators, t.user_name) AS operators, COALESCE(wo.workplaces, 1) AS workplaces,
                   round(extract(epoch FROM COALESCE(t.ended_at, now()) - t.started_at) / 3600.0, 1) AS hours,
                   t.fuel_volume, t.fuel_amount, t.fuel_cash, t.fuel_cashless, t.fuel_mixed, t.shop_amount, t.hose_counters_delta,
                   p.pay_cash, p.pay_card, p.pay_contract, p.pay_fuel_card,
                   (SELECT count(*) FROM fuel_sales fs WHERE fs.station_id = t.station_id AND fs.session_id = t.session_id AND fs.volume = 0) AS aborted,
                   (SELECT count(*) FROM fuel_sales fs WHERE fs.station_id = t.station_id AND fs.session_id = t.session_id AND fs.volume > 0) AS orders
            FROM v_shift_totals t JOIN v_station_names sn ON sn.station_id = t.station_id
            {PayByShift}
            {OperatorsByShift}
            WHERE {ShiftWhere}
            ORDER BY t.started_at DESC
            LIMIT 1000
            """, f!.Args);
        return Results.Ok(rows);
    }

    private static async Task<IResult> Operators(HttpContext ctx, StationScope scope, UiDb db)
    {
        var (f, error) = await UiFilter.ParseAsync(ctx, scope);
        if (error is not null) return error;
        await using var c = await db.OpenConnectionAsync(ctx.RequestAborted);
        // 1.11: sales go to the operator of the workplace they were made at (sysLocalSessions), not to whoever opened
        // the shift; a shift counts for every operator who worked in it; hours are each operator's own time in the shift.
        // A shift without local shifts (or a sale without one) belongs to the shift's UserName.
        var rows = await c.QueryAsync($"""
            WITH s AS (
                SELECT t.station_id, t.session_id, t.user_name, t.started_at, t.ended_at
                FROM shifts t JOIN v_station_names sn ON sn.station_id = t.station_id WHERE {ShiftWhere}),
            w AS (
                SELECT v.station_id, v.session_id, v.local_session_id, v.operator, v.started_at, COALESCE(v.ended_at, s.ended_at) AS ended_at
                FROM s JOIN v_workplace_shifts v ON v.station_id = s.station_id AND v.session_id = s.session_id
                UNION ALL
                SELECT s.station_id, s.session_id, NULL, s.user_name, s.started_at, s.ended_at FROM s
                WHERE NOT EXISTS (SELECT 1 FROM workplace_shifts x WHERE x.station_id = s.station_id AND x.session_id = s.session_id)),
            per AS (
                SELECT operator, count(*) AS shifts, count(DISTINCT station_id) AS stations,
                       sum(secs) AS secs
                FROM (SELECT station_id, session_id, operator, extract(epoch FROM max(COALESCE(ended_at, now())) - min(started_at)) AS secs
                      FROM w GROUP BY 1, 2, 3) x
                GROUP BY operator),
            o AS (
                SELECT COALESCE(v.operator, s.user_name) AS operator,
                       sum(fs.amount) AS revenue, sum(fs.volume) FILTER (WHERE fs.volume > 0 AND fs.payment_kind <> 'none') AS liters,
                       count(*) FILTER (WHERE fs.volume > 0 AND fs.payment_kind <> 'none') AS orders, count(*) FILTER (WHERE fs.volume = 0) AS aborted,
                       sum(fs.volume) FILTER (WHERE fs.payment_kind = 'none' AND fs.volume > 0) AS service_liters, sum(fuel_discount(fs.amount_discount, fs.price, fs.price_pump, fs.volume)) AS discounts
                FROM fuel_sales fs JOIN s ON s.station_id = fs.station_id AND s.session_id = fs.session_id
                LEFT JOIN v_workplace_shifts v ON v.station_id = fs.station_id AND v.local_session_id = fs.local_session_id AND v.session_id = fs.session_id
                GROUP BY 1)
            SELECT COALESCE(p.operator, o.operator) AS user_name, COALESCE(p.shifts, 0) AS shifts, COALESCE(p.stations, 0) AS stations,
                   round(COALESCE(p.secs, 0) / 3600.0, 1) AS hours,
                   COALESCE(o.revenue, 0) AS revenue, COALESCE(o.liters, 0) AS liters, COALESCE(o.orders, 0) AS orders,
                   COALESCE(o.aborted, 0) AS aborted, COALESCE(o.service_liters, 0) AS service_liters, COALESCE(o.discounts, 0) AS discounts,
                   round(COALESCE(o.revenue, 0) / NULLIF(p.shifts, 0), 2) AS revenue_per_shift,
                   round(o.aborted::numeric / NULLIF(o.orders + o.aborted, 0), 4) AS aborted_share
            FROM per p FULL JOIN o ON o.operator = p.operator
            ORDER BY revenue DESC
            """, f!.Args);
        return Results.Ok(rows);
    }

    /// <summary>The Topaz shift report (reconciled with a real Topaz export, see docs/topaz-schema.md).</summary>
    private static async Task<IResult> Card(Guid station, long session, HttpContext ctx, StationScope scope, UiDb db)
    {
        if (!await scope.CanSeeAsync(ctx.User, station)) return Results.Forbid();
        await using var c = await db.OpenConnectionAsync(ctx.RequestAborted);
        var args = new { station, session };
        var header = await c.QuerySingleOrDefaultAsync($"""
            SELECT t.station_id, t.session_id, t.session_num, t.user_name, t.started_at, t.ended_at, t.fuel_volume, t.fuel_amount,
                   t.fuel_cash, t.fuel_cashless, t.fuel_mixed, t.shop_amount, t.hose_counters_delta,
                   sn.station, sn.time_zone, p.*, COALESCE(wo.operators, t.user_name) AS operators, COALESCE(wo.workplaces, 1) AS workplaces,
                   round(extract(epoch FROM COALESCE(t.ended_at, now()) - t.started_at) / 3600.0, 1) AS hours
            FROM v_shift_totals t JOIN v_station_names sn ON sn.station_id = t.station_id
            {PayByShift}
            {OperatorsByShift}
            WHERE t.station_id = @station AND t.session_id = @session
            """, args);
        if (header is null) return Results.NotFound();

        var tanks = await c.QueryAsync("""
            SELECT t.tank_id, t.tank_num, COALESCE(t.tank_name, '') AS tank_name, t.fuel_name, t.price, t.start_volume, t.receipt_volume, t.pump_volume,
                   t.start_volume + t.receipt_volume - t.pump_volume AS book_end_volume, t.fact_volume, t.loss_volume, t.surplus_volume,
                   t.density, t.temperature, t.height, t.water, vt.max_volume
            FROM tank_shift_states t LEFT JOIN v_tanks vt ON vt.station_id = t.station_id AND vt.tank_id = t.tank_id
            WHERE t.station_id = @station AND t.session_id = @session ORDER BY t.tank_num
            """, args);
        // Workplaces (1.11): each local shift with its operator and its own sales (fuel by payment category, shop).
        var workplaces = await c.QueryAsync("""
            SELECT w.local_session_id, w.workbench_id, w.workplace, w.workplace_name, w.computer_name, w.is_main, w.operator,
                   w.started_at, w.ended_at,
                   COALESCE(f.liters, 0) AS liters, COALESCE(f.revenue, 0) AS revenue, COALESCE(f.orders, 0) AS orders, COALESCE(f.aborted, 0) AS aborted,
                   f.pay_cash, f.pay_card, f.pay_contract, f.pay_fuel_card,
                   COALESCE(sh.shop_amount, 0) AS shop_amount, COALESCE(sh.receipts, 0) AS receipts
            FROM v_workplace_shifts w
            LEFT JOIN LATERAL (
                SELECT sum(v.volume) FILTER (WHERE v.is_sale) AS liters, sum(v.amount) AS revenue,
                       count(*) FILTER (WHERE v.is_sale) AS orders, count(*) FILTER (WHERE v.volume = 0) AS aborted,
                       sum(v.amount) FILTER (WHERE v.pay_category = 'cash') AS pay_cash, sum(v.amount) FILTER (WHERE v.pay_category = 'card') AS pay_card,
                       sum(v.amount) FILTER (WHERE v.pay_category = 'contract') AS pay_contract, sum(v.amount) FILTER (WHERE v.pay_category = 'fuel_card') AS pay_fuel_card
                FROM fuel_sales fs JOIN v_fuel_sales_local v ON v.station_id = fs.station_id AND v.order_id = fs.order_id
                WHERE fs.station_id = w.station_id AND fs.session_id = w.session_id AND fs.local_session_id = w.local_session_id) f ON true
            LEFT JOIN LATERAL (
                SELECT sum(l.total) AS shop_amount, count(DISTINCT r.receipt_id) AS receipts
                FROM shop_receipts r JOIN shop_receipt_lines l ON l.station_id = r.station_id AND l.receipt_id = r.receipt_id
                WHERE r.station_id = w.station_id AND r.session_id = w.session_id AND r.local_session_id = w.local_session_id) sh ON true
            WHERE w.station_id = @station AND w.session_id = @session
            ORDER BY w.is_main DESC, w.started_at, w.local_session_id
            """, args);
        var hoses = await c.QueryAsync("""
            SELECT hose_id, pump_num, num_in_pump, tank_num, fuel_name, start_counter, end_counter, end_counter - start_counter AS delta
            FROM hose_shift_counters WHERE station_id = @station AND session_id = @session ORDER BY pump_num, num_in_pump
            """, args);
        var fuel = await c.QueryAsync("""
            SELECT tank_num, hose_num, fuel_name, payment_mode, remark, agent_name, card_code, volume, amount, price, price_paid, order_count,
                   amount_cash, amount_cashless, amount_discount, discount, amount_bonuses
            FROM v_shift_fuel_outcomes WHERE station_id = @station AND session_id = @session ORDER BY fuel_name, payment_mode, remark
            """, args);
        var cash = await c.QueryAsync("""
            SELECT doc_type_id, doc_type, cash_id, cash_name, operations, amount
            FROM v_shift_cash_flow WHERE station_id = @station AND session_id = @session ORDER BY doc_type_id, cash_id
            """, args);
        var items = await c.QueryAsync("""
            SELECT item_id, item_name, payment_mode, partner_name, is_return, quantity, amount, price, lines
            FROM v_shift_item_sales WHERE station_id = @station AND session_id = @session ORDER BY item_name, payment_mode
            """, args);
        var rests = await c.QueryAsync("""
            SELECT r.item_id, r.item_name, r.quantity
            FROM shifts s, item_rests_at(s.station_id, COALESCE(s.ended_at, now())) r
            WHERE s.station_id = @station AND s.session_id = @session ORDER BY r.item_name
            """, args);
        var orders = await c.QueryAsync("""
            SELECT order_id, order_num, local_ts, fuel_name, tank_num, hose_num, payment_mode, remark, payment_kind, pay_category, volume, volume_ordered,
                   price, price_pump, fuel_price_paid(price, price_pump) AS price_paid, amount, amount_cash, amount_cashless, amount_discount,
                   fuel_discount(amount_discount, price, price_pump, volume) AS discount, card_code, ws.operator, ws.workplace
            FROM v_fuel_sales_local v
            LEFT JOIN LATERAL (
                SELECT w.operator, w.workplace FROM fuel_sales f
                JOIN v_workplace_shifts w ON w.station_id = f.station_id AND w.local_session_id = f.local_session_id
                WHERE f.station_id = v.station_id AND f.order_id = v.order_id) ws ON true
            WHERE v.station_id = @station AND v.session_id = @session ORDER BY order_num
            """, args);
        var receipts = await c.QueryAsync("""
            SELECT r.receipt_id, r.received_at, r.tank_id, vt.num AS tank_num, f.name AS fuel_name, r.doc_number, r.car_number,
                   r.doc_volume, r.measured_volume, r.discrepancy, r.doc_mass, r.doc_density, r.doc_price
            FROM fuel_receipts r
            LEFT JOIN v_tanks vt ON vt.station_id = r.station_id AND vt.tank_id = r.tank_id
            LEFT JOIN v_fuels f ON f.station_id = r.station_id AND f.fuel_id = r.fuel_id
            WHERE r.station_id = @station AND r.session_id = @session ORDER BY r.received_at
            """, args);
        return Results.Ok(new { header, workplaces, tanks, hoses, fuel, cash, items, rests, orders, receipts });
    }
}
