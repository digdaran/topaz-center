using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;
using Topaz.Central.Processing;

namespace Topaz.Central.Ui;

public sealed record FuelCostBody(Guid StationId, long FuelId, DateTimeOffset ValidFrom, decimal PricePerLiter, long? ReceiptId, string? Comment, bool AllStationsWithSameType = false);
public sealed record ItemCostBody(Guid? StationId, string ItemKey, DateTimeOffset ValidFrom, decimal Cost, string? Comment);
public sealed record ItemCostImport(Guid? StationId, string Csv, DateTimeOffset? ValidFrom);
public sealed record PlanBody(Guid StationId, string Month, string Metric, string? FuelType, decimal? Value);

/// <summary>Stage 3: purchase costs, finance (margins, cash), plans. Owners and managers only.</summary>
public static partial class MoneyEndpoints
{
    public static void MapUiMoney(this RouteGroupBuilder ui)
    {
        var money = ui.MapGroup("").RequireAuthorization(Policies.Manager);
        money.MapGet("/analytics/finance", Finance);
        money.MapGet("/costs/fuel", FuelCosts);
        money.MapPost("/costs/fuel", AddFuelCost);
        money.MapDelete("/costs/fuel/{id:long}", DeleteFuelCost);
        money.MapGet("/costs/items", ItemCosts);
        money.MapPost("/costs/items", AddItemCost);
        money.MapPost("/costs/items/import", ImportItemCosts);
        money.MapDelete("/costs/items/{id:long}", DeleteItemCost);
        ui.MapGet("/plans", Plans);
        money.MapPut("/plans", SetPlan);
    }

    // ---------------------------------------------------------------- finance

    private static async Task<IResult> Finance(HttpContext ctx, StationScope scope, NpgsqlDataSource db)
    {
        var (fp, error) = await UiFilter.ParseAsync(ctx, scope);
        if (error is not null) return error;
        var f = fp!;
        var par = new SalesAnalyticsEndpoints.Par(db, ctx.RequestAborted);
        const string fuelMoney = """
            COALESCE(sum(revenue), 0) AS revenue, COALESCE(sum(liters), 0) AS liters, sum(cost_amount) AS cost,
            COALESCE(sum(costed_revenue), 0) - COALESCE(sum(cost_amount), 0) AS margin,
            round(COALESCE(sum(costed_liters), 0) / NULLIF(sum(liters), 0), 4) AS coverage,
            round((COALESCE(sum(costed_revenue), 0) - COALESCE(sum(cost_amount), 0)) / NULLIF(sum(costed_liters), 0), 2) AS margin_per_liter,
            round((COALESCE(sum(costed_revenue), 0) - COALESCE(sum(cost_amount), 0)) / NULLIF(sum(costed_revenue), 0), 4) AS margin_pct,
            COALESCE(sum(discounts), 0) AS discounts, sum(service_cost) AS service_cost, COALESCE(sum(service_liters), 0) AS service_liters
            """;
        const string shopMoney = """
            COALESCE(sum(amount), 0) AS revenue, sum(cost_amount) AS cost,
            COALESCE(sum(costed_amount), 0) - COALESCE(sum(cost_amount), 0) AS margin,
            round(COALESCE(sum(costed_amount), 0) / NULLIF(sum(amount), 0), 4) AS coverage,
            round((COALESCE(sum(costed_amount), 0) - COALESCE(sum(cost_amount), 0)) / NULLIF(sum(cost_amount), 0), 4) AS markup_pct,
            round((COALESCE(sum(costed_amount), 0) - COALESCE(sum(cost_amount), 0)) / NULLIF(sum(costed_amount), 0), 4) AS margin_pct
            """;
        var fuelTotalT = par.Q($"SELECT {fuelMoney} FROM v_agg_fuel WHERE {AnalyticsSql.AggFuelWhere}", f.Args);
        var shopTotalT = par.Q($"SELECT {shopMoney} FROM v_agg_shop WHERE {AnalyticsSql.AggWhere}", f.Args);
        var byStationT = par.Q($"""
            WITH fu AS (SELECT station_id, {fuelMoney} FROM v_agg_fuel WHERE {AnalyticsSql.AggFuelWhere} GROUP BY 1),
                 sh AS (SELECT station_id, {shopMoney} FROM v_agg_shop WHERE {AnalyticsSql.AggWhere} GROUP BY 1)
            SELECT sn.station_id, sn.station, fu.revenue AS fuel_revenue, fu.cost AS fuel_cost, fu.margin AS fuel_margin, fu.coverage AS fuel_coverage,
                   fu.margin_per_liter, fu.liters, sh.revenue AS shop_revenue, sh.cost AS shop_cost, sh.margin AS shop_margin, sh.coverage AS shop_coverage,
                   COALESCE(fu.margin, 0) + COALESCE(sh.margin, 0) AS margin, COALESCE(fu.revenue, 0) + COALESCE(sh.revenue, 0) AS revenue
            FROM v_station_names sn LEFT JOIN fu ON fu.station_id = sn.station_id LEFT JOIN sh ON sh.station_id = sn.station_id
            WHERE sn.station_id = ANY(@st) ORDER BY margin DESC
            """, f.Args);
        var byFuelT = par.Q($"""
            SELECT ft.name AS fuel, {fuelMoney} FROM v_agg_fuel a JOIN fuel_types ft ON ft.code = a.fuel_type
            WHERE {AnalyticsSql.AggFuelWhere} GROUP BY ft.name, ft.sort ORDER BY ft.sort
            """, f.Args);
        var byGroupT = par.Q($"""
            SELECT COALESCE(group_name, 'Без группы') AS group_name, {shopMoney} FROM v_agg_shop
            WHERE {AnalyticsSql.AggWhere} GROUP BY 1 ORDER BY 2 DESC
            """, f.Args);
        var seriesT = par.Q($"""
            WITH fu AS (SELECT {AnalyticsSql.DayBucket(f)} AS bucket, sum(revenue) AS fuel_revenue,
                               COALESCE(sum(costed_revenue), 0) - COALESCE(sum(cost_amount), 0) AS fuel_margin
                        FROM v_agg_fuel WHERE {AnalyticsSql.AggFuelWhere} GROUP BY 1),
                 sh AS (SELECT {AnalyticsSql.DayBucket(f)} AS bucket, sum(amount) AS shop_revenue,
                               COALESCE(sum(costed_amount), 0) - COALESCE(sum(cost_amount), 0) AS shop_margin
                        FROM agg_shop_daily WHERE {AnalyticsSql.AggWhere} GROUP BY 1)
            SELECT to_char(COALESCE(fu.bucket, sh.bucket), 'YYYY-MM-DD"T"HH24:MI') AS bucket, COALESCE(fuel_revenue, 0) AS fuel_revenue,
                   COALESCE(fuel_margin, 0) AS fuel_margin, COALESCE(shop_revenue, 0) AS shop_revenue, COALESCE(shop_margin, 0) AS shop_margin
            FROM fu FULL JOIN sh ON sh.bucket = fu.bucket ORDER BY 1
            """, new { st = f.Stations, from = f.From, to = f.To, fuel = f.Fuel, utcFrom = f.UtcFrom, utcTo = f.UtcTo });
        // Money flow by day: fuel and shop by payment kind, cash in/out from the money register.
        var cashT = par.Q($"""
            WITH fu AS (SELECT local_date, sum(revenue) FILTER (WHERE payment_kind = 'cash') AS fuel_cash,
                               sum(revenue) FILTER (WHERE payment_kind IN ('cashless', 'mixed')) AS fuel_cashless
                        FROM v_agg_fuel WHERE {AnalyticsSql.AggWhere} GROUP BY 1),
                 sh AS (SELECT (r.doc_at AT TIME ZONE sn.time_zone)::date AS local_date,
                               sum(CASE WHEN r.is_return THEN -r.amount_cash ELSE r.amount_cash END) AS shop_cash,
                               sum(CASE WHEN r.is_return THEN -r.amount_cashless ELSE r.amount_cashless END) AS shop_cashless
                        FROM shop_receipts r JOIN v_station_names sn ON sn.station_id = r.station_id
                        WHERE r.station_id = ANY(@st) AND r.doc_at >= @utcFrom AND r.doc_at < @utcTo GROUP BY 1),
                 rg AS (SELECT (m.moved_at AT TIME ZONE sn.time_zone)::date AS local_date,
                               sum(m.amount) FILTER (WHERE m.doc_type_id = 14) AS cash_in, -sum(m.amount) FILTER (WHERE m.doc_type_id = 15) AS cash_out
                        FROM cash_moves m JOIN v_station_names sn ON sn.station_id = m.station_id
                        WHERE m.station_id = ANY(@st) AND m.moved_at >= @utcFrom AND m.moved_at < @utcTo AND m.doc_type_id IN (14, 15) GROUP BY 1)
            SELECT d::date::text AS day, COALESCE(fu.fuel_cash, 0) AS fuel_cash, COALESCE(fu.fuel_cashless, 0) AS fuel_cashless,
                   COALESCE(sh.shop_cash, 0) AS shop_cash, COALESCE(sh.shop_cashless, 0) AS shop_cashless,
                   COALESCE(rg.cash_in, 0) AS cash_in, COALESCE(rg.cash_out, 0) AS cash_out
            FROM generate_series(@from::date, @to::date, interval '1 day') d
            LEFT JOIN fu ON fu.local_date = d::date LEFT JOIN sh ON sh.local_date = d::date LEFT JOIN rg ON rg.local_date = d::date
            ORDER BY 1
            """, f.Args);

        return Results.Ok(new
        {
            filter = OverviewEndpoints.Describe(f),
            fuel = (await fuelTotalT).Single(), shop = (await shopTotalT).Single(),
            by_station = await byStationT, by_fuel = await byFuelT, by_group = await byGroupT, series = await seriesT, cash = await cashT,
        });
    }

    // ---------------------------------------------------------------- fuel costs

    private static async Task<IResult> FuelCosts(HttpContext ctx, StationScope scope, NpgsqlDataSource db)
    {
        var (fp, error) = await UiFilter.ParseAsync(ctx, scope);
        if (error is not null) return error;
        var f = fp!;
        await using var c = await db.OpenConnectionAsync();
        var receipts = await c.QueryAsync("""
            SELECT r.station_id, sn.station, r.receipt_id, r.received_at, r.fuel_id, sf.fuel_name, r.doc_number, r.doc_volume, r.measured_volume,
                   fc.id AS cost_id, fc.price_per_liter, fc.comment,
                   (SELECT p.price_per_liter FROM fuel_costs p WHERE p.station_id = r.station_id AND p.fuel_id = r.fuel_id AND p.valid_from <= r.received_at
                    ORDER BY p.valid_from DESC LIMIT 1) AS effective_price
            FROM fuel_receipts r
            JOIN v_station_names sn ON sn.station_id = r.station_id
            LEFT JOIN v_station_fuels sf ON sf.station_id = r.station_id AND sf.fuel_id = r.fuel_id
            LEFT JOIN fuel_costs fc ON fc.station_id = r.station_id AND fc.receipt_id = r.receipt_id
            WHERE r.station_id = ANY(@st) AND r.received_at >= @utcFrom AND r.received_at < @utcTo
            ORDER BY r.received_at DESC
            """, f.Args);
        var costs = await c.QueryAsync("""
            SELECT fc.id, fc.station_id, sn.station, fc.fuel_id, sf.fuel_name, fc.valid_from, fc.price_per_liter, fc.receipt_id, fc.comment, fc.created_by, fc.created_at
            FROM fuel_costs fc JOIN v_station_names sn ON sn.station_id = fc.station_id
            LEFT JOIN v_station_fuels sf ON sf.station_id = fc.station_id AND sf.fuel_id = fc.fuel_id
            WHERE fc.station_id = ANY(@st) ORDER BY fc.valid_from DESC LIMIT 500
            """, new { st = f.Stations });
        var fuels = await c.QueryAsync("""
            SELECT f.station_id, sn.station, f.fuel_id, f.fuel_name, f.fuel_type,
                   (SELECT p.price_per_liter FROM fuel_costs p WHERE p.station_id = f.station_id AND p.fuel_id = f.fuel_id AND p.valid_from <= now()
                    ORDER BY p.valid_from DESC LIMIT 1) AS current_cost,
                   (SELECT price FROM fuel_sales s WHERE s.station_id = f.station_id AND s.fuel_id = f.fuel_id ORDER BY sold_at DESC LIMIT 1) AS current_price
            FROM v_station_fuels f JOIN v_station_names sn ON sn.station_id = f.station_id
            WHERE f.station_id = ANY(@st) AND NOT f.is_deleted AND f.fuel_name NOT LIKE '<%' ORDER BY sn.station, f.fuel_name
            """, new { st = f.Stations });
        return Results.Ok(new { receipts, costs, fuels });
    }

    private static async Task<IResult> AddFuelCost(FuelCostBody body, ClaimsPrincipal user, StationScope scope, NpgsqlDataSource db, RollupRefresher rollups)
    {
        if (!await scope.CanSeeAsync(user, body.StationId)) return Results.Forbid();
        if (body.PricePerLiter is <= 0 or > 10_000) return Results.Problem(statusCode: 400, title: "Цена за литр должна быть больше 0");
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        // The same price for all allowed stations selling this fuel type (convenience for a shared supplier price).
        var targets = new List<(Guid Station, long Fuel)> { (body.StationId, body.FuelId) };
        if (body.AllStationsWithSameType)
        {
            var allowed = await scope.AllowedAsync(user);
            targets = (await c.QueryAsync<(Guid, long)>("""
                SELECT f.station_id, f.fuel_id FROM v_station_fuels f
                WHERE f.station_id = ANY(@allowed) AND NOT f.is_deleted
                  AND f.fuel_type = (SELECT fuel_type FROM v_station_fuels WHERE station_id = @s AND fuel_id = @fuel)
                """, new { allowed, s = body.StationId, fuel = body.FuelId }, tx)).ToList();
        }
        if (body.ReceiptId is not null)
            await c.ExecuteAsync("DELETE FROM fuel_costs WHERE station_id = @s AND receipt_id = @r", new { s = body.StationId, r = body.ReceiptId }, tx);
        foreach (var (station, fuel) in targets)
        {
            await c.ExecuteAsync("""
                INSERT INTO fuel_costs (station_id, fuel_id, valid_from, price_per_liter, receipt_id, comment, created_by)
                VALUES (@station, @fuel, @from, @price, @receipt, @comment, @by)
                """, new { station, fuel, from = body.ValidFrom.ToUniversalTime(), price = body.PricePerLiter, receipt = station == body.StationId ? body.ReceiptId : null, comment = body.Comment, by = user.Identity?.Name }, tx);
            await c.ExecuteAsync("INSERT INTO rollup_dirty (station_id, kind) VALUES (@station, 'all')", new { station }, tx);
        }
        await tx.CommitAsync();
        rollups.Notify();
        await Audit.WriteAsync(db, user, "fuel_cost_added", $"station:{body.StationId}/fuel:{body.FuelId}", new { body.ValidFrom, body.PricePerLiter, body.ReceiptId, stations = targets.Count });
        return Results.Ok(new { stations = targets.Count });
    }

    private static async Task<IResult> DeleteFuelCost(long id, ClaimsPrincipal user, StationScope scope, NpgsqlDataSource db, RollupRefresher rollups)
    {
        await using var c = await db.OpenConnectionAsync();
        var station = await c.QuerySingleOrDefaultAsync<Guid?>("SELECT station_id FROM fuel_costs WHERE id = @id", new { id });
        if (station is null) return Results.NotFound();
        if (!await scope.CanSeeAsync(user, station.Value)) return Results.Forbid();
        await c.ExecuteAsync("DELETE FROM fuel_costs WHERE id = @id; INSERT INTO rollup_dirty (station_id, kind) VALUES (@station, 'all')", new { id, station });
        rollups.Notify();
        await Audit.WriteAsync(db, user, "fuel_cost_deleted", $"fuel_cost:{id}", null);
        return Results.NoContent();
    }

    // ---------------------------------------------------------------- item costs

    private static async Task<IResult> ItemCosts(HttpContext ctx, StationScope scope, NpgsqlDataSource db)
    {
        var st = await scope.AllowedAsync(ctx.User);
        await using var c = await db.OpenConnectionAsync();
        var items = await c.QueryAsync("""
            WITH keys AS (
                SELECT k.item_key, max(k.name) AS name, max(k.group_name) AS group_name, max(k.item_code) AS item_code, count(DISTINCT k.station_id) AS stations
                FROM v_item_keys k WHERE k.station_id = ANY(@st) GROUP BY 1),
            price AS (
                SELECT k.item_key, round(avg(l.price), 2) AS retail_price
                FROM shop_receipt_lines l JOIN v_item_keys k ON k.station_id = l.station_id AND k.item_id = l.item_id
                JOIN shop_receipts r ON r.station_id = l.station_id AND r.receipt_id = l.receipt_id
                WHERE l.station_id = ANY(@st) AND r.doc_at > now() - interval '60 days' GROUP BY 1),
            cost AS (
                SELECT DISTINCT ON (item_key) item_key, id AS cost_id, cost, valid_from, station_id AS cost_station
                FROM item_costs WHERE station_id IS NULL OR station_id = ANY(@st)
                ORDER BY item_key, valid_from DESC, (station_id IS NULL))
            SELECT keys.*, price.retail_price, cost.cost_id, cost.cost, cost.valid_from, cost.cost_station,
                   round((price.retail_price - cost.cost) / NULLIF(cost.cost, 0), 4) AS markup
            FROM keys LEFT JOIN price USING (item_key) LEFT JOIN cost USING (item_key)
            ORDER BY keys.group_name NULLS LAST, keys.name
            """, new { st });
        var history = await c.QueryAsync("""
            SELECT ic.id, ic.item_key, ic.station_id, sn.station, ic.valid_from, ic.cost, ic.comment, ic.created_by, ic.created_at
            FROM item_costs ic LEFT JOIN v_station_names sn ON sn.station_id = ic.station_id
            WHERE ic.station_id IS NULL OR ic.station_id = ANY(@st) ORDER BY ic.created_at DESC LIMIT 500
            """, new { st });
        return Results.Ok(new { items, history });
    }

    private static async Task<IResult> AddItemCost(ItemCostBody body, ClaimsPrincipal user, StationScope scope, NpgsqlDataSource db, RollupRefresher rollups)
    {
        if (body.StationId is null ? !user.IsInRole(Roles.Owner) : !await scope.CanSeeAsync(user, body.StationId.Value))
            return Results.Problem(statusCode: 403, title: "Цену для всех АЗС может задать только владелец");
        if (body.Cost <= 0 || string.IsNullOrWhiteSpace(body.ItemKey)) return Results.Problem(statusCode: 400, title: "Укажите товар и цену больше 0");
        await using var c = await db.OpenConnectionAsync();
        await InsertItemCosts(c, null, [(body.ItemKey, body.Cost)], body.StationId, body.ValidFrom, body.Comment, user, await scope.AllowedAsync(user));
        rollups.Notify();
        await Audit.WriteAsync(db, user, "item_cost_added", $"item:{body.ItemKey}", body);
        return Results.NoContent();
    }

    [GeneratedRegex(@"^\d{6,14}$")]
    private static partial Regex Barcode();

    /// <summary>
    /// CSV ("штрихкод или название; цена[; дата]", separator ; or tab, decimal comma or point) → item costs.
    /// Rows are matched to item keys by barcode, else by normalized name; unmatched rows are reported back.
    /// </summary>
    private static async Task<IResult> ImportItemCosts(ItemCostImport body, ClaimsPrincipal user, StationScope scope, NpgsqlDataSource db, RollupRefresher rollups)
    {
        if (body.StationId is null ? !user.IsInRole(Roles.Owner) : !await scope.CanSeeAsync(user, body.StationId.Value))
            return Results.Problem(statusCode: 403, title: "Цены для всех АЗС может загрузить только владелец");
        var allowed = await scope.AllowedAsync(user);
        await using var c = await db.OpenConnectionAsync();
        var keys = (await c.QueryAsync<(string Key, string Name, string? Code)>(
            "SELECT DISTINCT item_key, name, item_code FROM v_item_keys WHERE station_id = ANY(@allowed)", new { allowed })).ToList();
        var byCode = keys.Where(k => !string.IsNullOrWhiteSpace(k.Code)).GroupBy(k => k.Code!.Trim()).ToDictionary(g => g.Key, g => g.First().Key);
        var byName = keys.GroupBy(k => Norm(k.Name)).ToDictionary(g => g.Key, g => g.First().Key);

        var matched = new List<(string Key, decimal Cost, DateTimeOffset? From)>();
        var unmatched = new List<object>();
        var lineNo = 0;
        foreach (var raw in body.Csv.Split('\n'))
        {
            lineNo++;
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0) continue;
            var parts = line.Split(line.Contains('\t') ? '\t' : ';').Select(p => p.Trim().Trim('"')).ToArray();
            if (parts.Length < 2 || !decimal.TryParse(parts[1].Replace(" ", "").Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var cost) || cost <= 0)
            {
                if (lineNo > 1) unmatched.Add(new { line = lineNo, text = line, reason = "нет цены" }); // first line may be a header
                continue;
            }
            DateTimeOffset? from = parts.Length > 2 && DateTime.TryParseExact(parts[2], ["dd.MM.yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? new DateTimeOffset(d, TimeSpan.FromHours(3)) : null;
            var id = parts[0];
            var key = Barcode().IsMatch(id) && byCode.TryGetValue(id, out var k1) ? k1 : byName.TryGetValue(Norm(id), out var k2) ? k2 : null;
            if (key is null) unmatched.Add(new { line = lineNo, text = line, reason = "товар не найден" });
            else matched.Add((key, cost, from));
        }
        await using var tx = await c.BeginTransactionAsync();
        foreach (var g in matched.GroupBy(m => m.From ?? body.ValidFrom ?? DateTimeOffset.UtcNow))
            await InsertItemCosts(c, tx, g.Select(m => (m.Key, m.Cost)).ToList(), body.StationId, g.Key, "импорт CSV", user, allowed);
        await tx.CommitAsync();
        rollups.Notify();
        await Audit.WriteAsync(db, user, "item_costs_imported", body.StationId is null ? "all stations" : $"station:{body.StationId}", new { matched = matched.Count, unmatched = unmatched.Count });
        return Results.Ok(new { matched = matched.Count, unmatched });
    }

    private static string Norm(string s) => Regex.Replace(s.Trim().ToLowerInvariant(), @"\s+", " ");

    private static async Task InsertItemCosts(NpgsqlConnection c, NpgsqlTransaction? tx, IReadOnlyList<(string Key, decimal Cost)> rows, Guid? station,
        DateTimeOffset from, string? comment, ClaimsPrincipal user, Guid[] allowed)
    {
        foreach (var (key, cost) in rows)
            await c.ExecuteAsync("INSERT INTO item_costs (station_id, item_key, valid_from, cost, comment, created_by) VALUES (@station, @key, @from, @cost, @comment, @by)",
                new { station, key, from = from.ToUniversalTime(), cost, comment, by = user.Identity?.Name }, tx);
        // Margins of every affected station are recomputed (all of them for an "all stations" price).
        await c.ExecuteAsync("INSERT INTO rollup_dirty (station_id, kind) SELECT s, 'all' FROM unnest(@ids) AS s",
            new { ids = station is { } s ? new[] { s } : allowed }, tx);
    }

    private static async Task<IResult> DeleteItemCost(long id, ClaimsPrincipal user, StationScope scope, NpgsqlDataSource db, RollupRefresher rollups)
    {
        await using var c = await db.OpenConnectionAsync();
        var row = await c.QuerySingleOrDefaultAsync<(long Id, Guid? Station)?>("SELECT id, station_id FROM item_costs WHERE id = @id", new { id });
        if (row is null) return Results.NotFound();
        if (row.Value.Station is null ? !user.IsInRole(Roles.Owner) : !await scope.CanSeeAsync(user, row.Value.Station.Value)) return Results.Forbid();
        var allowed = await scope.AllowedAsync(user);
        await c.ExecuteAsync("DELETE FROM item_costs WHERE id = @id", new { id });
        await c.ExecuteAsync("INSERT INTO rollup_dirty (station_id, kind) SELECT s, 'all' FROM unnest(@ids) AS s",
            new { ids = row.Value.Station is { } s ? new[] { s } : allowed });
        rollups.Notify();
        await Audit.WriteAsync(db, user, "item_cost_deleted", $"item_cost:{id}", null);
        return Results.NoContent();
    }

    // ---------------------------------------------------------------- plans

    /// <summary>Plans of a month with facts to date, a forecast to the month end and the daily cumulative track.</summary>
    private static async Task<IResult> Plans(HttpContext ctx, StationScope scope, NpgsqlDataSource db, string? month)
    {
        var allowed = await scope.AllowedAsync(ctx.User);
        var m = DateTime.TryParseExact((month ?? "") + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var mm)
            ? mm : new DateTime(DateTime.UtcNow.AddHours(3).Year, DateTime.UtcNow.AddHours(3).Month, 1);
        var end = m.AddMonths(1).AddDays(-1);
        await using var c = await db.OpenConnectionAsync();
        var plans = (await c.QueryAsync<(Guid StationId, string Metric, string FuelType, decimal Value)>(
            "SELECT station_id, metric, fuel_type, value FROM sales_plans WHERE station_id = ANY(@allowed) AND month = @m", new { allowed, m })).ToList();
        // History: 8 weeks before the month for the forecast, plus the month itself.
        var hist = new { allowed, hfrom = m.AddDays(-56), end };
        var fuel = (await c.QueryAsync<(Guid StationId, DateTime Day, string FuelType, decimal Liters, decimal Revenue)>("""
            SELECT station_id, local_date::timestamp, fuel_type, sum(liters), sum(revenue) FROM v_agg_fuel
            WHERE station_id = ANY(@allowed) AND local_date BETWEEN @hfrom AND @end GROUP BY 1, 2, 3
            """, hist)).ToList();
        var shop = (await c.QueryAsync<(Guid StationId, DateTime Day, decimal Amount)>("""
            SELECT station_id, local_date::timestamp, sum(amount) FROM agg_shop_receipts_daily
            WHERE station_id = ANY(@allowed) AND local_date BETWEEN @hfrom AND @end GROUP BY 1, 2
            """, hist)).ToList();
        var stations = (await c.QueryAsync<(Guid Id, string Name)>("SELECT station_id, station FROM v_station_names WHERE station_id = ANY(@allowed) ORDER BY station", new { allowed })).ToList();
        var today = DateTime.UtcNow.AddHours(3).Date; // Moscow; month-level precision is enough here
        var lastActual = today > end ? end : today.AddDays(-1);

        Dictionary<DateTime, decimal> Daily(Guid station, string metric, string fuelType) => metric switch
        {
            "fuel_liters" => fuel.Where(x => x.StationId == station && (fuelType == "" || x.FuelType == fuelType)).GroupBy(x => x.Day).ToDictionary(g => g.Key, g => g.Sum(x => x.Liters)),
            "fuel_revenue" => fuel.Where(x => x.StationId == station).GroupBy(x => x.Day).ToDictionary(g => g.Key, g => g.Sum(x => x.Revenue)),
            _ => shop.Where(x => x.StationId == station).GroupBy(x => x.Day).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount)),
        };

        var rows = new List<object>();
        foreach (var (stationId, name) in stations)
        {
            var metrics = plans.Where(p => p.StationId == stationId).Select(p => (p.Metric, p.FuelType)).ToHashSet();
            foreach (var std in new[] { ("fuel_liters", ""), ("fuel_revenue", ""), ("shop_revenue", "") }) metrics.Add(std);
            foreach (var (metric, fuelType) in metrics.OrderBy(x => x.Metric).ThenBy(x => x.FuelType))
            {
                var daily = Daily(stationId, metric, fuelType);
                var fact = daily.Where(d => d.Key >= m && d.Key <= end).Sum(d => d.Value);
                var forecastRest = lastActual < end && lastActual >= m.AddDays(-1)
                    ? Forecast.Daily(daily, lastActual, (end - lastActual).Days).Sum(p => p.Value) : 0;
                var plan = plans.FirstOrDefault(p => p.StationId == stationId && p.Metric == metric && p.FuelType == fuelType).Value;
                var daysTotal = (end - m).Days + 1;
                var daysPassed = Math.Clamp((lastActual - m).Days + 1, 0, daysTotal);
                var expectedToDate = plan > 0 ? Math.Round(plan * daysPassed / daysTotal, 2) : (decimal?)null;
                var cumulative = 0m;
                var track = Enumerable.Range(0, daysTotal).Select(i =>
                {
                    var day = m.AddDays(i);
                    cumulative += day <= lastActual && daily.TryGetValue(day, out var v) ? v : 0;
                    return new { day = day.ToString("yyyy-MM-dd"), fact = day <= lastActual ? cumulative : (decimal?)null, plan = plan > 0 ? Math.Round(plan * (i + 1) / daysTotal, 2) : (decimal?)null };
                }).ToList();
                rows.Add(new
                {
                    station_id = stationId, station = name, metric, fuel_type = fuelType, plan = plan > 0 ? plan : (decimal?)null, fact = Math.Round(fact, 2),
                    done = plan > 0 ? Math.Round(fact / plan, 4) : (decimal?)null,
                    expected_to_date = expectedToDate, gap = expectedToDate is { } e ? Math.Round(fact - e, 2) : (decimal?)null,
                    forecast = Math.Round(fact + forecastRest, 2), forecast_done = plan > 0 ? Math.Round((fact + forecastRest) / plan, 4) : (decimal?)null,
                    days_total = daysTotal, days_passed = daysPassed, track,
                });
            }
        }
        return Results.Ok(new { month = m.ToString("yyyy-MM"), rows });
    }

    private static async Task<IResult> SetPlan(PlanBody body, ClaimsPrincipal user, StationScope scope, NpgsqlDataSource db)
    {
        if (!await scope.CanSeeAsync(user, body.StationId)) return Results.Forbid();
        if (body.Metric is not ("fuel_liters" or "fuel_revenue" or "shop_revenue")) return Results.Problem(statusCode: 400, title: "Неизвестный показатель плана");
        if (!DateTime.TryParseExact(body.Month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var m))
            return Results.Problem(statusCode: 400, title: "Месяц в формате ГГГГ-ММ");
        await using var c = await db.OpenConnectionAsync();
        if (body.Value is null or 0)
            await c.ExecuteAsync("DELETE FROM sales_plans WHERE station_id = @s AND month = @m AND metric = @metric AND fuel_type = @ft",
                new { s = body.StationId, m, metric = body.Metric, ft = body.FuelType ?? "" });
        else
            await c.ExecuteAsync("""
                INSERT INTO sales_plans (station_id, month, metric, fuel_type, value, updated_by) VALUES (@s, @m, @metric, @ft, @v, @by)
                ON CONFLICT (station_id, month, metric, fuel_type) DO UPDATE SET value = EXCLUDED.value, updated_by = EXCLUDED.updated_by, updated_at = now()
                """, new { s = body.StationId, m, metric = body.Metric, ft = body.FuelType ?? "", v = body.Value, by = user.Identity?.Name });
        await Audit.WriteAsync(db, user, "plan_set", $"station:{body.StationId}/{body.Month}/{body.Metric}/{body.FuelType}", new { body.Value });
        return Results.NoContent();
    }
}
