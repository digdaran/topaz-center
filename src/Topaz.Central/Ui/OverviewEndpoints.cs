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

    public const string FuelKpiColumns = """
        COALESCE(sum(amount), 0) AS fuel_revenue,
        COALESCE(sum(volume) FILTER (WHERE is_sale), 0) AS liters,
        count(*) FILTER (WHERE is_sale) AS orders,
        count(*) FILTER (WHERE volume = 0) AS aborted,
        COALESCE(sum(volume) FILTER (WHERE payment_kind = 'none' AND volume > 0), 0) AS service_liters,
        COALESCE(sum(amount) FILTER (WHERE payment_kind = 'cashless'), 0) AS cashless,
        COALESCE(sum(amount) FILTER (WHERE payment_kind = 'mixed'), 0) AS mixed,
        COALESCE(sum(amount_discount), 0) AS discounts
        """;

    public const string ShopKpiColumns = """
        COALESCE(sum(amount), 0) AS shop_revenue,
        count(DISTINCT receipt_id) AS shop_receipts,
        COALESCE(sum(quantity), 0) AS shop_items
        """;

    public static async Task<Kpi> KpiAsync(NpgsqlConnection c, UiFilter f)
    {
        var kpi = await c.QuerySingleAsync<Kpi>($"SELECT {FuelKpiColumns} FROM v_fuel_sales_local WHERE {FuelWhere}", f.Args);
        if (f.Fuel is null)
        {
            var shop = await c.QuerySingleAsync<Kpi>($"SELECT {ShopKpiColumns} FROM v_shop_lines_local WHERE {ShopWhere}", f.Args);
            kpi.ShopRevenue = shop.ShopRevenue;
            kpi.ShopReceipts = shop.ShopReceipts;
            kpi.ShopItems = shop.ShopItems;
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
        var (f, error) = await UiFilter.ParseAsync(ctx, scope);
        if (error is not null) return error;
        await using var c = await db.OpenConnectionAsync(ctx.RequestAborted);

        var current = await AnalyticsSql.KpiAsync(c, f!);
        var cmp = f!.ComparePeriod();
        var previous = cmp is null ? null : await AnalyticsSql.KpiAsync(c, cmp);

        var series = await c.QueryAsync($"""
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

        var byFuel = await c.QueryAsync($"""
            SELECT COALESCE(fuel_name, '?') AS fuel, sum(volume) FILTER (WHERE is_sale) AS liters, sum(amount) AS revenue
            FROM v_fuel_sales_local WHERE {AnalyticsSql.FuelWhere} GROUP BY 1 ORDER BY 3 DESC NULLS LAST
            """, f.Args);

        var stations = await StationRows(c, f);

        var offline = await c.QueryAsync("""
            SELECT v.id AS station_id, COALESCE(NULLIF(s.display_name, ''), v.name) AS station, v.last_seen_at,
                   extract(epoch FROM v.silent_for)::bigint AS silent_seconds, v.agent_queue_pending, v.agent_queue_dead, v.topaz_db_ok
            FROM v_station_status v JOIN stations s ON s.id = v.id
            WHERE v.id = ANY(@st) AND v.status = 'active'
              AND (v.silent_for > interval '15 minutes' OR v.silent_for IS NULL OR v.agent_queue_dead > 0 OR v.topaz_db_ok = false)
            ORDER BY v.silent_for DESC NULLS FIRST
            """, new { st = f.Stations });

        return Results.Ok(new { filter = Describe(f), current, previous, series, by_fuel = byFuel, stations, problems = offline });
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
        WITH fuel AS (SELECT station_id, {AnalyticsSql.FuelKpiColumns}, count(DISTINCT local_date) AS active_days
                      FROM v_fuel_sales_local WHERE {AnalyticsSql.FuelWhere} GROUP BY station_id),
             shop AS (SELECT station_id, {AnalyticsSql.ShopKpiColumns}
                      FROM v_shop_lines_local WHERE {AnalyticsSql.ShopWhere} AND @fuel::text IS NULL GROUP BY station_id)
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
