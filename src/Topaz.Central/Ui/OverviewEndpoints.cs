using Dapper;
using Npgsql;

namespace Topaz.Central.Ui;

/// <summary>KPIs shared by Overview, station comparison and plans. Definitions: docs/web.md.</summary>
public sealed class Kpi
{
    public decimal FuelRevenue { get; set; }
    public decimal Liters { get; set; }
    public long Orders { get; set; }
    public long Aborted { get; set; }
    public decimal ServiceLiters { get; set; }
    public decimal Cashless { get; set; }
    public decimal Mixed { get; set; }
    public decimal Discounts { get; set; }
    public decimal ShopRevenue { get; set; }
    public long ShopReceipts { get; set; }
    public decimal ShopItems { get; set; }

    // Money (owners/managers only; Redact() clears them for viewers).
    public decimal? FuelCost { get; set; }
    public decimal? FuelCostedRevenue { get; set; }
    public decimal? CostedLiters { get; set; }
    public decimal? ShopCost { get; set; }
    public decimal? ShopCostedAmount { get; set; }

    public decimal? Margin => FuelCost is null && ShopCost is null ? null
        : (FuelCostedRevenue ?? 0) - (FuelCost ?? 0) + (ShopCostedAmount ?? 0) - (ShopCost ?? 0);
    public decimal? FuelCoverage => CostedLiters is { } cl && Liters > 0 ? Math.Round(cl / Liters, 4) : null;

    public Kpi Redact()
    {
        FuelCost = FuelCostedRevenue = CostedLiters = ShopCost = ShopCostedAmount = null;
        return this;
    }

    public decimal Revenue => FuelRevenue + ShopRevenue;
    public decimal? AvgFill => Orders > 0 ? Math.Round(Liters / Orders, 2) : null;
    public decimal? AvgFuelCheck => Orders > 0 ? Math.Round(FuelRevenue / Orders, 2) : null;
    public decimal? AvgShopCheck => ShopReceipts > 0 ? Math.Round(ShopRevenue / ShopReceipts, 2) : null;
    public decimal? CashlessShare => FuelRevenue > 0 ? Math.Round(Cashless / FuelRevenue, 4) : null;
    public decimal? AbortedShare => Orders + Aborted > 0 ? Math.Round((decimal)Aborted / (Orders + Aborted), 4) : null;
}

public static class AnalyticsSql
{
    public const string FuelWhere = """
        station_id = ANY(@st) AND sold_at >= @utcFrom AND sold_at < @utcTo
        AND local_date BETWEEN @from::date AND @to::date AND (@fuel::text IS NULL OR fuel_name = @fuel)
        """;

    public const string ShopWhere = """
        station_id = ANY(@st) AND doc_at >= @utcFrom AND doc_at < @utcTo AND local_date BETWEEN @from::date AND @to::date
        """;

    /// <summary>Day-level filter on the rollups (agg_*): exact local days, no time-of-day.</summary>
    public const string AggWhere = "station_id = ANY(@st) AND local_date BETWEEN @from::date AND @to::date";
    public const string AggFuelWhere = AggWhere + " AND (@fuel::text IS NULL OR fuel_name = @fuel)";

    public const string AggFuelKpiColumns = """
        COALESCE(sum(revenue), 0) AS fuel_revenue,
        COALESCE(sum(liters), 0) AS liters,
        COALESCE(sum(orders), 0) AS orders,
        COALESCE(sum(aborted), 0) AS aborted,
        COALESCE(sum(service_liters), 0) AS service_liters,
        COALESCE(sum(revenue) FILTER (WHERE payment_kind = 'cashless'), 0) AS cashless,
        COALESCE(sum(revenue) FILTER (WHERE payment_kind = 'mixed'), 0) AS mixed,
        COALESCE(sum(discounts), 0) AS discounts,
        sum(cost_amount) AS fuel_cost, COALESCE(sum(costed_revenue), 0) AS fuel_costed_revenue, COALESCE(sum(costed_liters), 0) AS costed_liters
        """;

    /// <summary>Bucket of a rollup day for day/week/month granularity.</summary>
    public static string DayBucket(UiFilter f) => f.Granularity switch
    {
        "week" => "date_trunc('week', local_date::timestamp)",
        "month" => "date_trunc('month', local_date::timestamp)",
        _ => "local_date::timestamp",
    };

    public static async Task<Kpi> KpiAsync(NpgsqlConnection c, UiFilter f)
    {
        var kpi = await c.QuerySingleAsync<Kpi>($"SELECT {AggFuelKpiColumns} FROM v_agg_fuel WHERE {AggFuelWhere}", f.Args);
        if (f.Fuel is null)
        {
            var shop = await c.QuerySingleAsync<Kpi>($"""
                SELECT COALESCE(sum(amount), 0) AS shop_revenue, COALESCE(sum(receipts), 0) AS shop_receipts,
                       (SELECT COALESCE(sum(quantity), 0) FROM agg_shop_daily WHERE {AggWhere}) AS shop_items,
                       (SELECT sum(cost_amount) FROM agg_shop_daily WHERE {AggWhere}) AS shop_cost,
                       (SELECT COALESCE(sum(costed_amount), 0) FROM agg_shop_daily WHERE {AggWhere}) AS shop_costed_amount
                FROM agg_shop_receipts_daily WHERE {AggWhere}
                """, f.Args);
            kpi.ShopRevenue = shop.ShopRevenue;
            kpi.ShopReceipts = shop.ShopReceipts;
            kpi.ShopItems = shop.ShopItems;
            kpi.ShopCost = shop.ShopCost;
            kpi.ShopCostedAmount = shop.ShopCostedAmount;
        }
        return kpi;
    }
}

public static class OverviewEndpoints
{
    public static void MapUiOverview(this RouteGroupBuilder ui)
    {
        ui.MapGet("/analytics/overview", Overview);
        ui.MapGet("/analytics/stations", StationComparison);
    }

    private static async Task<IResult> Overview(HttpContext ctx, StationScope scope, NpgsqlDataSource db)
    {
        var (fp, error) = await UiFilter.ParseAsync(ctx, scope);
        if (error is not null) return error;
        var f = fp!;
        var ct = ctx.RequestAborted;
        var par = new SalesAnalyticsEndpoints.Par(db, ct);
        async Task<T> With<T>(Func<NpgsqlConnection, Task<T>> run)
        {
            await using var c = await db.OpenConnectionAsync(ct);
            return await run(c);
        }

        var cmp = f.ComparePeriod();
        var currentT = With(c => AnalyticsSql.KpiAsync(c, f));
        var previousT = cmp is null ? Task.FromResult<Kpi?>(null) : With<Kpi?>(async c => await AnalyticsSql.KpiAsync(c, cmp));
        var seriesT = f.Granularity != "hour" ? par.Q($"""
            WITH fuel AS (
                SELECT {AnalyticsSql.DayBucket(f)} AS bucket, sum(revenue) AS fuel_revenue, sum(liters) AS liters, sum(orders) AS orders
                FROM v_agg_fuel WHERE {AnalyticsSql.AggFuelWhere} GROUP BY 1),
            shop AS (
                SELECT {AnalyticsSql.DayBucket(f)} AS bucket, sum(amount) AS shop_revenue, sum(receipts) AS shop_receipts
                FROM agg_shop_receipts_daily WHERE {AnalyticsSql.AggWhere} AND @fuel::text IS NULL GROUP BY 1)
            SELECT to_char(COALESCE(fuel.bucket, shop.bucket), 'YYYY-MM-DD"T"HH24:MI') AS bucket,
                   COALESCE(fuel_revenue, 0) AS fuel_revenue, COALESCE(liters, 0) AS liters, COALESCE(orders, 0) AS orders,
                   COALESCE(shop_revenue, 0) AS shop_revenue, COALESCE(shop_receipts, 0) AS shop_receipts
            FROM fuel FULL JOIN shop ON shop.bucket = fuel.bucket
            ORDER BY 1
            """, f.Args) : par.Q($"""
            WITH fuel AS (
                SELECT {f.Bucket("local_ts")} AS bucket, sum(amount) AS fuel_revenue, sum(volume) FILTER (WHERE is_sale) AS liters,
                       count(*) FILTER (WHERE is_sale) AS orders
                FROM v_fuel_sales_local WHERE {AnalyticsSql.FuelWhere} GROUP BY 1),
            shop AS (
                SELECT {f.Bucket("local_ts")} AS bucket, sum(amount) AS shop_revenue, count(DISTINCT receipt_id) AS shop_receipts
                FROM v_shop_lines_local WHERE {AnalyticsSql.ShopWhere} AND @fuel::text IS NULL GROUP BY 1)
            SELECT to_char(COALESCE(fuel.bucket, shop.bucket), 'YYYY-MM-DD"T"HH24:MI') AS bucket,
                   COALESCE(fuel_revenue, 0) AS fuel_revenue, COALESCE(liters, 0) AS liters, COALESCE(orders, 0) AS orders,
                   COALESCE(shop_revenue, 0) AS shop_revenue, COALESCE(shop_receipts, 0) AS shop_receipts
            FROM fuel FULL JOIN shop ON shop.bucket = fuel.bucket
            ORDER BY 1
            """, f.Args);
        var byFuelT = par.Q($"""
            SELECT ft.name AS fuel, sum(a.liters) AS liters, sum(a.revenue) AS revenue
            FROM v_agg_fuel a JOIN fuel_types ft ON ft.code = a.fuel_type
            WHERE {AnalyticsSql.AggFuelWhere} GROUP BY ft.name, ft.sort ORDER BY 3 DESC NULLS LAST
            """, f.Args);
        var stationsT = With(c => StationRows(c, f));
        var problemsT = par.Q("""
            SELECT v.id AS station_id, COALESCE(NULLIF(s.display_name, ''), v.name) AS station, v.last_seen_at,
                   extract(epoch FROM v.silent_for)::bigint AS silent_seconds, v.agent_queue_pending, v.agent_queue_dead, v.topaz_db_ok
            FROM v_station_status v JOIN stations s ON s.id = v.id
            WHERE v.id = ANY(@st) AND v.status = 'active'
              AND (v.silent_for > interval '15 minutes' OR v.silent_for IS NULL OR v.agent_queue_dead > 0 OR v.topaz_db_ok = false)
            ORDER BY v.silent_for DESC NULLS FIRST
            """, new { st = f.Stations });

        return Results.Ok(new
        {
            filter = Describe(f),
            current = Roles.SeesMoney(ctx.User) ? await currentT : (await currentT).Redact(),
            previous = Roles.SeesMoney(ctx.User) ? await previousT : (await previousT)?.Redact(), series = await seriesT, by_fuel = await byFuelT,
            stations = await stationsT, problems = await problemsT,
        });
    }

    private static async Task<IResult> StationComparison(HttpContext ctx, StationScope scope, NpgsqlDataSource db)
    {
        var (f, error) = await UiFilter.ParseAsync(ctx, scope);
        if (error is not null) return error;
        await using var c = await db.OpenConnectionAsync(ctx.RequestAborted);
        return Results.Ok(new { filter = Describe(f!), stations = await StationRows(c, f!) });
    }

    /// <summary>One KPI row per station (ranking / comparison).</summary>
    internal static async Task<IEnumerable<dynamic>> StationRows(NpgsqlConnection c, UiFilter f) => await c.QueryAsync($"""
        WITH fuel AS (SELECT station_id, {AnalyticsSql.AggFuelKpiColumns}, count(DISTINCT local_date) AS active_days
                      FROM v_agg_fuel WHERE {AnalyticsSql.AggFuelWhere} GROUP BY station_id),
             shop AS (SELECT station_id, sum(amount) AS shop_revenue, sum(receipts) AS shop_receipts
                      FROM agg_shop_receipts_daily WHERE {AnalyticsSql.AggWhere} AND @fuel::text IS NULL GROUP BY station_id)
        SELECT sn.station_id, sn.station, sn.region, sn.group_name,
               COALESCE(fuel.fuel_revenue, 0) AS fuel_revenue, COALESCE(fuel.liters, 0) AS liters, COALESCE(fuel.orders, 0) AS orders,
               COALESCE(fuel.aborted, 0) AS aborted, COALESCE(fuel.cashless, 0) AS cashless, COALESCE(fuel.discounts, 0) AS discounts,
               COALESCE(shop.shop_revenue, 0) AS shop_revenue, COALESCE(shop.shop_receipts, 0) AS shop_receipts,
               COALESCE(fuel.fuel_revenue, 0) + COALESCE(shop.shop_revenue, 0) AS revenue,
               round(COALESCE(fuel.liters, 0) / @days, 2) AS liters_per_day,
               round(fuel.liters / NULLIF(fuel.orders, 0), 2) AS avg_fill,
               round(fuel.fuel_revenue / NULLIF(fuel.orders, 0), 2) AS avg_fuel_check,
               round(shop.shop_revenue / NULLIF(shop.shop_receipts, 0), 2) AS avg_shop_check,
               round(fuel.cashless / NULLIF(fuel.fuel_revenue, 0), 4) AS cashless_share,
               round(shop.shop_revenue / NULLIF(fuel.fuel_revenue, 0), 4) AS shop_to_fuel
        FROM v_station_names sn
        LEFT JOIN fuel ON fuel.station_id = sn.station_id
        LEFT JOIN shop ON shop.station_id = sn.station_id
        WHERE sn.station_id = ANY(@st)
        ORDER BY revenue DESC
        """, new { st = f.Stations, from = f.From, to = f.To, utcFrom = f.UtcFrom, utcTo = f.UtcTo, fuel = f.Fuel, days = (decimal)f.Days });

    internal static object Describe(UiFilter f) => new
    {
        stations = f.Stations,
        from = f.From.ToString("yyyy-MM-dd"),
        to = f.To.ToString("yyyy-MM-dd"),
        g = f.Granularity,
        compare = f.Compare,
        compare_from = f.ComparePeriod()?.From.ToString("yyyy-MM-dd"),
        compare_to = f.ComparePeriod()?.To.ToString("yyyy-MM-dd"),
        fuel = f.Fuel,
        days = f.Days,
    };
}
