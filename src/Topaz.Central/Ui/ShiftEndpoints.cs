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

    private static async Task<IResult> List(HttpContext ctx, StationScope scope, UiDb db)
    {
        var (f, error) = await UiFilter.ParseAsync(ctx, scope);
        if (error is not null) return error;
        await using var c = await db.OpenConnectionAsync(ctx.RequestAborted);
        var rows = await c.QueryAsync($"""
            SELECT t.station_id, sn.station, t.session_id, t.session_num, t.user_name, t.started_at, t.ended_at,
                   round(extract(epoch FROM COALESCE(t.ended_at, now()) - t.started_at) / 3600.0, 1) AS hours,
                   t.fuel_volume, t.fuel_amount, t.fuel_cash, t.fuel_cashless, t.fuel_mixed, t.shop_amount, t.hose_counters_delta,
                   (SELECT count(*) FROM fuel_sales fs WHERE fs.station_id = t.station_id AND fs.session_id = t.session_id AND fs.volume = 0) AS aborted,
                   (SELECT count(*) FROM fuel_sales fs WHERE fs.station_id = t.station_id AND fs.session_id = t.session_id AND fs.volume > 0) AS orders
            FROM v_shift_totals t JOIN v_station_names sn ON sn.station_id = t.station_id
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
        var rows = await c.QueryAsync($"""
            WITH s AS (
                SELECT t.station_id, t.session_id, t.user_name, t.started_at, t.ended_at
                FROM shifts t JOIN v_station_names sn ON sn.station_id = t.station_id WHERE {ShiftWhere}),
            o AS (
                SELECT fs.station_id, fs.session_id, sum(fs.amount) AS revenue, sum(fs.volume) FILTER (WHERE fs.volume > 0 AND fs.payment_kind <> 'none') AS liters,
                       count(*) FILTER (WHERE fs.volume > 0 AND fs.payment_kind <> 'none') AS orders, count(*) FILTER (WHERE fs.volume = 0) AS aborted,
                       sum(fs.volume) FILTER (WHERE fs.payment_kind = 'none' AND fs.volume > 0) AS service_liters, sum(fs.amount_discount) AS discounts
                FROM fuel_sales fs JOIN s ON s.station_id = fs.station_id AND s.session_id = fs.session_id
                GROUP BY 1, 2)
            SELECT s.user_name, count(*) AS shifts, count(DISTINCT s.station_id) AS stations,
                   round(sum(extract(epoch FROM COALESCE(s.ended_at, now()) - s.started_at)) / 3600.0, 1) AS hours,
                   COALESCE(sum(o.revenue), 0) AS revenue, COALESCE(sum(o.liters), 0) AS liters, COALESCE(sum(o.orders), 0) AS orders,
                   COALESCE(sum(o.aborted), 0) AS aborted, COALESCE(sum(o.service_liters), 0) AS service_liters, COALESCE(sum(o.discounts), 0) AS discounts,
                   round(COALESCE(sum(o.revenue), 0) / count(*), 2) AS revenue_per_shift,
                   round(sum(o.aborted)::numeric / NULLIF(sum(o.orders) + sum(o.aborted), 0), 4) AS aborted_share
            FROM s LEFT JOIN o ON o.station_id = s.station_id AND o.session_id = s.session_id
            GROUP BY s.user_name
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
        var header = await c.QuerySingleOrDefaultAsync("""
            SELECT t.*, sn.station, sn.time_zone,
                   round(extract(epoch FROM COALESCE(t.ended_at, now()) - t.started_at) / 3600.0, 1) AS hours
            FROM v_shift_totals t JOIN v_station_names sn ON sn.station_id = t.station_id
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
        var hoses = await c.QueryAsync("""
            SELECT hose_id, pump_num, num_in_pump, tank_num, fuel_name, start_counter, end_counter, end_counter - start_counter AS delta
            FROM hose_shift_counters WHERE station_id = @station AND session_id = @session ORDER BY pump_num, num_in_pump
            """, args);
        var fuel = await c.QueryAsync("""
            SELECT tank_num, hose_num, fuel_name, payment_mode, remark, agent_name, card_code, volume, amount, price, order_count,
                   amount_cash, amount_cashless, amount_discount, amount_bonuses
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
            SELECT order_id, order_num, local_ts, fuel_name, tank_num, hose_num, payment_mode, remark, payment_kind, volume, volume_ordered,
                   price, price_pump, amount, amount_cash, amount_cashless, amount_discount, card_code
            FROM v_fuel_sales_local WHERE station_id = @station AND session_id = @session ORDER BY order_num
            """, args);
        var receipts = await c.QueryAsync("""
            SELECT r.receipt_id, r.received_at, r.tank_id, vt.num AS tank_num, f.name AS fuel_name, r.doc_number, r.car_number,
                   r.doc_volume, r.measured_volume, r.discrepancy, r.doc_mass, r.doc_density, r.doc_price
            FROM fuel_receipts r
            LEFT JOIN v_tanks vt ON vt.station_id = r.station_id AND vt.tank_id = r.tank_id
            LEFT JOIN v_fuels f ON f.station_id = r.station_id AND f.fuel_id = r.fuel_id
            WHERE r.station_id = @station AND r.session_id = @session ORDER BY r.received_at
            """, args);
        return Results.Ok(new { header, tanks, hoses, fuel, cash, items, rests, orders, receipts });
    }
}
