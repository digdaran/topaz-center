using Dapper;
using Npgsql;

namespace Topaz.Central.Ui;

/// <summary>Stage 2 analytics: fuel, demand, goods. Definitions: docs/web.md.</summary>
public static class SalesAnalyticsEndpoints
{
    public static void MapUiSalesAnalytics(this RouteGroupBuilder ui)
    {
        ui.MapGet("/analytics/fuel", Fuel);
        ui.MapGet("/analytics/demand", Demand);
        ui.MapGet("/analytics/items", Items);
    }

    private static async Task<(UiFilter?, IResult?, NpgsqlConnection?)> Open(HttpContext ctx, StationScope scope, UiDb db)
    {
        var (f, error) = await UiFilter.ParseAsync(ctx, scope);
        return error is not null ? (null, error, null) : (f, null, await db.OpenConnectionAsync(ctx.RequestAborted));
    }

    /// <summary>Runs a query on its own pooled connection so the queries of one page execute in parallel.</summary>
    internal sealed class Par(UiDb db, CancellationToken ct)
    {
        public async Task<List<dynamic>> Q(string sql, object args)
        {
            await using var c = await db.OpenConnectionAsync(ct);
            return (await c.QueryAsync(new CommandDefinition(sql, args, cancellationToken: ct))).ToList();
        }

        public async Task<List<T>> Q<T>(string sql, object args)
        {
            await using var c = await db.OpenConnectionAsync(ct);
            return (await c.QueryAsync<T>(new CommandDefinition(sql, args, cancellationToken: ct))).ToList();
        }
    }

    // ---------------------------------------------------------------- fuel

    private sealed record DailyPrice(Guid StationId, string Station, string FuelType, DateTime Day, decimal Liters, decimal Price);

    private static async Task<IResult> Fuel(HttpContext ctx, StationScope scope, UiDb db)
    {
        var (fp, error) = await UiFilter.ParseAsync(ctx, scope);
        if (error is not null) return error;
        var f = fp!;
        var par = new Par(db, ctx.RequestAborted);
        var w = AnalyticsSql.FuelWhere;

        var byTypeT = par.Q($"""
            SELECT ft.code AS fuel_type, ft.name AS fuel_name, sum(a.liters) AS liters, sum(a.revenue) AS revenue, sum(a.orders) AS orders,
                   round(sum(a.revenue) / NULLIF(sum(a.liters), 0), 2) AS avg_price,
                   round(sum(a.liters) / NULLIF(sum(a.orders), 0), 2) AS avg_fill,
                   round(sum(a.revenue) FILTER (WHERE a.payment_kind = 'cashless') / NULLIF(sum(a.revenue), 0), 4) AS cashless_share
            FROM v_agg_fuel a JOIN fuel_types ft ON ft.code = a.fuel_type
            WHERE {AnalyticsSql.AggFuelWhere} GROUP BY ft.code, ft.name, ft.sort ORDER BY ft.sort
            """, f.Args);

        var seriesT = f.Granularity == "hour" ? par.Q($"""
            SELECT to_char({f.Bucket("local_ts")}, 'YYYY-MM-DD"T"HH24:MI') AS bucket, fuel_type,
                   sum(volume) FILTER (WHERE is_sale) AS liters, sum(amount) AS revenue
            FROM v_fuel_sales_local WHERE {w} GROUP BY 1, 2 ORDER BY 1
            """, f.Args) : par.Q($"""
            SELECT to_char({AnalyticsSql.DayBucket(f)}, 'YYYY-MM-DD"T"HH24:MI') AS bucket, fuel_type, sum(liters) AS liters, sum(revenue) AS revenue
            FROM v_agg_fuel WHERE {AnalyticsSql.AggFuelWhere} GROUP BY 1, 2 ORDER BY 1
            """, f.Args);

        var paymentsT = par.Q($"""
            SELECT payment_kind, COALESCE(payment_mode, '?') AS payment_mode, sum(orders) AS orders,
                   sum(liters + service_liters) AS liters, sum(revenue) AS revenue, sum(discounts) AS discounts
            FROM v_agg_fuel WHERE {AnalyticsSql.AggFuelWhere} GROUP BY 1, 2
            HAVING sum(liters + service_liters) > 0 OR sum(revenue) <> 0
            ORDER BY revenue DESC NULLS LAST
            """, f.Args);

        // Fill size distribution and order type (Topaz: VolumeOrdered = 0 means "full tank").
        var fillsT = par.Q($"""
            SELECT CASE WHEN volume < 10 THEN 1 WHEN volume < 20 THEN 2 WHEN volume < 30 THEN 3 WHEN volume < 40 THEN 4
                        WHEN volume < 50 THEN 5 WHEN volume < 70 THEN 6 WHEN volume < 100 THEN 7 ELSE 8 END AS bin,
                   count(*) AS orders, sum(volume) AS liters, sum(amount) AS revenue
            FROM v_fuel_sales_local WHERE {w} AND is_sale GROUP BY 1 ORDER BY 1
            """, f.Args);
        var orderTypesT = par.Q($"""
            SELECT CASE WHEN volume_ordered = 0 THEN 'full_tank'
                        WHEN volume = round(volume) AND abs(volume - volume_ordered) <= 0.01 THEN 'by_liters'
                        -- prepaid a round sum (multiple of 100 ₽, ±1 ₽ for rounding/discount) for a non-whole volume
                        WHEN amount_cash + amount_cashless >= 100
                             AND abs(amount_cash + amount_cashless - round(amount_cash + amount_cashless, -2)) <= 1 THEN 'by_amount'
                        ELSE 'other' END AS kind,
                   count(*) AS orders, sum(volume) AS liters
            FROM v_fuel_sales_local WHERE {w} AND is_sale GROUP BY 1 ORDER BY 2 DESC
            """, f.Args);

        var hosesT = par.Q($"""
            SELECT station_id, station, hose_num, tank_num, fuel_type, COALESCE(fuel_name, '?') AS fuel_name,
                   count(*) FILTER (WHERE is_sale) AS orders, sum(volume) FILTER (WHERE is_sale) AS liters,
                   count(*) FILTER (WHERE volume = 0) AS aborted
            FROM v_fuel_sales_local WHERE {w} GROUP BY 1, 2, 3, 4, 5, 6 ORDER BY station, hose_num
            """, f.Args);

        var serviceT = par.Q($"""
            SELECT station, COALESCE(fuel_name, '?') AS fuel_name, COALESCE(payment_mode, '?') AS payment_mode, count(*) AS operations, sum(volume) AS liters
            FROM v_fuel_sales_local WHERE {w} AND payment_kind = 'none' AND volume > 0 GROUP BY 1, 2, 3 ORDER BY liters DESC
            """, f.Args);

        // Price and demand: daily liters and the day's price per station and fuel type, with ±28 days around the period.
        var wide = f with { From = f.From.AddDays(-28), To = f.To.AddDays(28) };
        var dailyT = (par.Q<DailyPrice>($"""
            SELECT station_id AS StationId, station AS Station, fuel_type AS FuelType, local_date AS Day, sum(liters) AS Liters,
                   (array_agg(last_price ORDER BY last_sold_at DESC) FILTER (WHERE orders > 0))[1] AS Price
            FROM v_agg_fuel WHERE {AnalyticsSql.AggFuelWhere} GROUP BY 1, 2, 3, 4 HAVING sum(orders) > 0
            """, wide.Args));

        var (byType, series, payments, fills, orderTypes, hoses, service) =
            (await byTypeT, await seriesT, await paymentsT, await fillsT, await orderTypesT, await hosesT, await serviceT);
        var daily = await dailyT;
        return Results.Ok(new
        {
            filter = OverviewEndpoints.Describe(f),
            by_type = byType, series, payments, fills, order_types = orderTypes, hoses, service,
            price_changes = PriceChanges(daily, f),
            price_history = daily.Where(d => d.Day >= f.From && d.Day <= f.To)
                .GroupBy(d => (d.FuelType, d.Day)).Select(g => new { fuel_type = g.Key.FuelType, day = g.Key.Day.ToString("yyyy-MM-dd"), price = Math.Round(g.Average(x => x.Price), 2), liters = g.Sum(x => x.Liters) })
                .OrderBy(x => x.day),
        });
    }

    /// <summary>
    /// For every price change inside the period: average liters/day over 14 days before and after (days with sales of
    /// that fuel), the price change % and the volume change %. Elasticity = Δvolume% / Δprice% (rough, other factors ignored).
    /// </summary>
    private static IEnumerable<object> PriceChanges(List<DailyPrice> daily, UiFilter f)
    {
        foreach (var g in daily.GroupBy(d => (d.StationId, d.Station, d.FuelType)))
        {
            var days = g.OrderBy(d => d.Day).ToList();
            for (var i = 1; i < days.Count; i++)
            {
                if (days[i].Price == days[i - 1].Price || days[i].Day < f.From || days[i].Day > f.To) continue;
                var change = days[i].Day;
                var before = days.Where(d => d.Day < change && d.Day >= change.AddDays(-14)).ToList();
                var after = days.Where(d => d.Day >= change && d.Day < change.AddDays(14)).ToList();
                if (before.Count < 5 || after.Count < 5) continue;
                var lb = before.Average(d => d.Liters);
                var la = after.Average(d => d.Liters);
                var dp = (days[i].Price - days[i - 1].Price) / days[i - 1].Price;
                var dv = lb > 0 ? (la - lb) / lb : 0;
                yield return new
                {
                    station_id = g.Key.StationId, station = g.Key.Station, fuel_type = g.Key.FuelType, date = change.ToString("yyyy-MM-dd"),
                    price_before = days[i - 1].Price, price_after = days[i].Price, price_change = Math.Round(dp, 4),
                    liters_per_day_before = Math.Round(lb, 1), liters_per_day_after = Math.Round(la, 1), volume_change = Math.Round(dv, 4),
                    elasticity = dp != 0 ? Math.Round(dv / dp, 2) : (decimal?)null,
                    days_before = before.Count, days_after = after.Count,
                };
            }
        }
    }

    // ---------------------------------------------------------------- demand

    private sealed record DayValue(DateTime Day, decimal Liters, decimal Revenue, long Orders);

    private static async Task<IResult> Demand(HttpContext ctx, StationScope scope, UiDb db)
    {
        var (fp, error) = await UiFilter.ParseAsync(ctx, scope);
        if (error is not null) return error;
        var f = fp!;
        var par = new Par(db, ctx.RequestAborted);

        // Average per calendar day of each weekday in the period (a weekday may occur 4 or 5 times).
        var weekdays = Enumerable.Range(0, f.Days).Select(i => f.From.AddDays(i)).GroupBy(d => ((int)d.DayOfWeek + 6) % 7 + 1).ToDictionary(g => g.Key, g => g.Count());
        var fuelHeatT = par.Q<(int Dow, int Hour, long Orders, decimal Liters, decimal Revenue)>($"""
            SELECT local_dow, local_hour, count(*) FILTER (WHERE is_sale), COALESCE(sum(volume) FILTER (WHERE is_sale), 0), COALESCE(sum(amount), 0)
            FROM v_fuel_sales_local WHERE {AnalyticsSql.FuelWhere} GROUP BY 1, 2
            """, f.Args);
        var shopHeatT = par.Q<(int Dow, int Hour, long Receipts, decimal Revenue)>($"""
            SELECT local_dow, local_hour, count(DISTINCT receipt_id), COALESCE(sum(amount), 0)
            FROM v_shop_lines_local WHERE {AnalyticsSql.ShopWhere} GROUP BY 1, 2
            """, f.Args);
        decimal Per(int dow, decimal v) => weekdays.TryGetValue(dow, out var n) && n > 0 ? Math.Round(v / n, 2) : 0;

        var dailyRowsT = (par.Q<DayValue>($"""
            SELECT local_date AS Day, COALESCE(sum(liters), 0) AS Liters, COALESCE(sum(revenue), 0) AS Revenue, COALESCE(sum(orders), 0) AS Orders
            FROM v_agg_fuel WHERE {AnalyticsSql.AggFuelWhere} GROUP BY 1 ORDER BY 1
            """, f.Args));
        var shopDailyT = (par.Q<(DateTime Day, decimal Revenue, long Receipts)>($"""
            SELECT local_date, COALESCE(sum(amount), 0), COALESCE(sum(receipts), 0)::bigint FROM agg_shop_receipts_daily WHERE {AnalyticsSql.AggWhere} GROUP BY 1
            """, f.Args));
        var monthsT = par.Q($"""
            SELECT to_char(date_trunc('month', local_date), 'YYYY-MM') AS month, sum(liters) AS liters, sum(revenue) AS revenue,
                   count(DISTINCT local_date) AS days, round(sum(liters) / NULLIF(count(DISTINCT local_date), 0), 1) AS liters_per_day
            FROM v_agg_fuel WHERE {AnalyticsSql.AggFuelWhere} GROUP BY 1 ORDER BY 1
            """, f.Args);

        var (fuelHeat, shopHeat, dailyRows, months) = (await fuelHeatT, await shopHeatT, await dailyRowsT, await monthsT);
        var shopDaily = (await shopDailyT).ToDictionary(x => x.Day);
        var byDay = dailyRows.ToDictionary(d => d.Day);
        var allDays = Enumerable.Range(0, f.Days).Select(i => f.From.AddDays(i)).ToList();
        decimal Ma(int i, Func<DayValue, decimal> sel)
        {
            var window = Enumerable.Range(Math.Max(0, i - 6), Math.Min(7, i + 1)).Select(j => byDay.TryGetValue(allDays[j], out var v) ? sel(v) : 0).ToList();
            return Math.Round(window.Average(), 2);
        }
        var trend = allDays.Select((d, i) => new
        {
            day = d.ToString("yyyy-MM-dd"),
            liters = byDay.TryGetValue(d, out var v) ? v.Liters : 0,
            revenue = v?.Revenue ?? 0,
            orders = v?.Orders ?? 0,
            shop_revenue = shopDaily.TryGetValue(d, out var s) ? s.Revenue : 0,
            shop_receipts = s.Receipts,
            liters_ma7 = Ma(i, x => x.Liters),
            revenue_ma7 = Ma(i, x => x.Revenue),
        }).ToList();

        // Forecast from the last day with data (not beyond today), 14 days ahead.
        var lastActual = dailyRows.Count > 0 ? dailyRows.Max(d => d.Day) : f.To;
        var forecastLiters = Forecast.Daily(dailyRows.ToDictionary(d => d.Day, d => d.Liters), lastActual, 14);
        var forecastRevenue = Forecast.Daily(dailyRows.ToDictionary(d => d.Day, d => d.Revenue), lastActual, 14);


        return Results.Ok(new
        {
            filter = OverviewEndpoints.Describe(f),
            weekday_counts = weekdays,
            fuel_heatmap = fuelHeat.Select(h => new { dow = h.Dow, hour = h.Hour, orders = Per(h.Dow, h.Orders), liters = Per(h.Dow, h.Liters), revenue = Per(h.Dow, h.Revenue) }),
            shop_heatmap = shopHeat.Select(h => new { dow = h.Dow, hour = h.Hour, receipts = Per(h.Dow, h.Receipts), revenue = Per(h.Dow, h.Revenue) }),
            trend,
            forecast = forecastLiters.Zip(forecastRevenue, (l, r) => new { day = l.Date.ToString("yyyy-MM-dd"), liters = l.Value, revenue = r.Value }),
            months,
        });
    }

    // ---------------------------------------------------------------- items

    private sealed record ItemRow(string ItemKey, string Name, string? GroupName, decimal Quantity, decimal Revenue, long Receipts, DateTime? LastSale);
    private sealed record StockRow(Guid StationId, string Station, string ItemKey, string Name, string? GroupName, decimal Stock, decimal Sold28, DateTime? LastSale, decimal? Price);

    private static async Task<IResult> Items(HttpContext ctx, StationScope scope, UiDb db)
    {
        var (fp, error) = await UiFilter.ParseAsync(ctx, scope);
        if (error is not null) return error;
        var f = fp!;
        var par = new Par(db, ctx.RequestAborted);
        var q = ctx.Request.Query;
        var coverDays = Math.Clamp(int.TryParse(q["cover_days"], out var cd) ? cd : 14, 1, 120);
        var deadDays = Math.Clamp(int.TryParse(q["dead_days"], out var dd) ? dd : 30, 7, 365);

        var groupsT = par.Q($"""
            SELECT COALESCE(group_name, 'Без группы') AS group_name, sum(amount) AS revenue, sum(quantity) AS quantity, sum(lines) AS lines,
                   count(DISTINCT item_key) AS items
            FROM v_agg_shop WHERE {AnalyticsSql.AggWhere} GROUP BY 1 ORDER BY 2 DESC
            """, f.Args);

        var itemsT = (par.Q<ItemRow>($"""
            SELECT COALESCE(item_key, 'id:' || item_id) AS ItemKey, max(item_name) AS Name, max(group_name) AS GroupName,
                   sum(quantity) AS Quantity, sum(amount) AS Revenue, sum(receipts)::bigint AS Receipts, max(local_date)::timestamp AS LastSale
            FROM v_agg_shop WHERE {AnalyticsSql.AggWhere} GROUP BY 1
            """, f.Args));

        // XYZ on weekly quantities over the period (needs at least 4 weeks).
        var weeklyT = (par.Q<(string ItemKey, DateTime Week, decimal Qty)>($"""
            SELECT COALESCE(item_key, 'id:' || item_id), date_trunc('week', local_date::timestamp), sum(quantity)
            FROM v_agg_shop WHERE {AnalyticsSql.AggWhere} GROUP BY 1, 2
            """, f.Args));
        var stockT = (par.Q<StockRow>("""
            WITH moves AS (
                SELECT station_id, item_id, sum(quantity) AS stock FROM v_item_moves WHERE station_id = ANY(@st) GROUP BY 1, 2),
            sales AS (
                SELECT a.station_id, a.item_id,
                       sum(a.quantity) FILTER (WHERE a.local_date >= (now() AT TIME ZONE sn.time_zone)::date - 28) AS sold28,
                       (max(a.local_date) FILTER (WHERE a.quantity > 0))::timestamp AS last_sale
                FROM agg_shop_daily a JOIN v_station_names sn ON sn.station_id = a.station_id
                WHERE a.station_id = ANY(@st) GROUP BY 1, 2)
            SELECT m.station_id AS StationId, sn.station AS Station, k.item_key AS ItemKey, k.name AS Name, k.group_name AS GroupName, m.stock AS Stock,
                   COALESCE(s.sold28, 0) AS Sold28, s.last_sale AS LastSale,
                   (SELECT (p.data->>'Value')::numeric FROM records p WHERE p.station_id = m.station_id AND p.dataset = 'dcItemPrices'
                     AND p.data->>'ItemID' = m.item_id::text ORDER BY (p.data->>'PriceID')::int DESC LIMIT 1) AS Price
            FROM moves m
            JOIN v_station_names sn ON sn.station_id = m.station_id
            JOIN v_item_keys k ON k.station_id = m.station_id AND k.item_id = m.item_id
            LEFT JOIN sales s ON s.station_id = m.station_id AND s.item_id = m.item_id
            WHERE m.stock <> 0 OR COALESCE(s.sold28, 0) > 0
            """, new { st = f.Stations }));
        var shopVsFuelT = par.Q($"""
            WITH fh AS (SELECT local_hour AS hour, count(*) FILTER (WHERE is_sale) AS orders FROM v_fuel_sales_local WHERE {AnalyticsSql.FuelWhere} GROUP BY 1),
                 sh AS (SELECT local_hour AS hour, count(DISTINCT (station_id, receipt_id)) AS receipts, sum(amount) AS revenue FROM v_shop_lines_local WHERE {AnalyticsSql.ShopWhere} GROUP BY 1)
            SELECT h AS hour, COALESCE(fh.orders, 0) AS fuel_orders, COALESCE(sh.receipts, 0) AS shop_receipts, COALESCE(sh.revenue, 0) AS shop_revenue,
                   round(COALESCE(sh.receipts, 0)::numeric / NULLIF(fh.orders, 0), 4) AS ratio
            FROM generate_series(0, 23) h LEFT JOIN fh ON fh.hour = h LEFT JOIN sh ON sh.hour = h ORDER BY h
            """, f.Args);
        var returnsT = par.Q($"""
            SELECT item_name, count(*) AS lines, -sum(quantity) AS quantity, -sum(amount) AS amount
            FROM v_shop_lines_local WHERE {AnalyticsSql.ShopWhere} AND is_return GROUP BY 1 ORDER BY 4 DESC
            """, f.Args);
        var writeOffsT = par.Q("""
            SELECT sn.station, k.name, -sum(m.quantity) AS quantity, count(*) AS acts
            FROM v_item_moves m JOIN v_station_names sn ON sn.station_id = m.station_id
            JOIN v_item_keys k ON k.station_id = m.station_id AND k.item_id = m.item_id
            WHERE m.station_id = ANY(@st) AND m.doc_type_id = 20 AND m.moved_at >= @utcFrom AND m.moved_at < @utcTo
              AND (m.moved_at AT TIME ZONE sn.time_zone)::date BETWEEN @from::date AND @to::date
            GROUP BY 1, 2 ORDER BY 3 DESC
            """, f.Args);
        var (groups, items, stock, shopVsFuel, returns, writeOffs) = (await groupsT, await itemsT, await stockT, await shopVsFuelT, await returnsT, await writeOffsT);
        var weekly = (await weeklyT).GroupBy(x => x.ItemKey).ToDictionary(g => g.Key, g => g.ToDictionary(x => x.Week, x => x.Qty));
        var firstWeek = StartOfWeek(f.From);
        var weeks = Enumerable.Range(0, (int)((StartOfWeek(f.To) - firstWeek).TotalDays / 7) + 1).Select(i => firstWeek.AddDays(7 * i)).ToList();
        // Partial first/last weeks distort variation: use full weeks only.
        var fullWeeks = weeks.Where(w => w >= f.From && w.AddDays(6) <= f.To).ToList();

        var abcRevenue = Forecast.Abc(items.Select(i => (i.ItemKey, i.Revenue)));
        var abcQty = Forecast.Abc(items.Select(i => (i.ItemKey, i.Quantity)));
        var totalRevenue = items.Sum(i => i.Revenue);
        var itemRows = items.OrderByDescending(i => i.Revenue).Select(i =>
        {
            var series = fullWeeks.Select(w => weekly.TryGetValue(i.ItemKey, out var m) && m.TryGetValue(w, out var v) ? v : 0).ToList();
            var (xyz, cv) = Forecast.Xyz(series);
            return new
            {
                item_key = i.ItemKey, name = i.Name, group_name = i.GroupName, quantity = i.Quantity, revenue = i.Revenue, receipts = i.Receipts,
                share = totalRevenue > 0 ? Math.Round(i.Revenue / totalRevenue, 4) : 0, avg_price = i.Quantity != 0 ? Math.Round(i.Revenue / i.Quantity, 2) : (decimal?)null,
                last_sale = i.LastSale, abc = abcRevenue[i.ItemKey], abc_qty = abcQty[i.ItemKey], xyz, cv,
            };
        }).ToList();
        var matrix = itemRows.GroupBy(i => (i.abc, i.xyz)).Select(g => new { abc = g.Key.abc, xyz = g.Key.xyz, items = g.Count(), revenue = g.Sum(x => x.revenue) });

        // Stock now (per station), sales speed over the last 28 days, days of cover, recommended order, dead stock.
        var now = DateTime.UtcNow;
        var stockRows = stock.Select(s =>
        {
            var perDay = Math.Round(s.Sold28 / 28m, 3);
            var cover = perDay > 0 ? Math.Round(s.Stock / perDay, 1) : (decimal?)null;
            var recommended = perDay > 0 ? Math.Max(0, Math.Ceiling(perDay * coverDays * 1.2m - s.Stock)) : 0;
            var daysSinceSale = s.LastSale is { } ls ? (int)(now - ls).TotalDays : (int?)null;
            var status = s.Stock <= 0 && perDay > 0 ? "out" : cover is { } cv2 && cv2 < 3 ? "low" :
                         s.Stock > 0 && (daysSinceSale is null || daysSinceSale >= deadDays) ? "dead" : cover is > 90 ? "overstock" : "ok";
            return new
            {
                station_id = s.StationId, station = s.Station, item_key = s.ItemKey, name = s.Name, group_name = s.GroupName, stock = s.Stock,
                sold_28d = s.Sold28, per_day = perDay, days_of_cover = cover, stockout_date = cover is { } cv3 && s.Stock > 0 ? DateTime.UtcNow.AddDays((double)cv3).ToString("yyyy-MM-dd") : null,
                recommended_order = recommended, last_sale = s.LastSale, days_since_sale = daysSinceSale, status,
                stock_value = s.Price is { } p ? Math.Round(p * Math.Max(0, s.Stock), 2) : (decimal?)null,
            };
        }).OrderBy(s => s.status switch { "out" => 0, "low" => 1, "dead" => 2, "overstock" => 3, _ => 4 }).ThenBy(s => s.days_of_cover ?? 9999).ToList();



        return Results.Ok(new
        {
            filter = OverviewEndpoints.Describe(f), cover_days = coverDays, dead_days = deadDays, full_weeks = fullWeeks.Count,
            groups, items = itemRows, matrix, stock = stockRows, shop_vs_fuel = shopVsFuel, returns, write_offs = writeOffs,
        });
    }

    private static DateTime StartOfWeek(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));
}
