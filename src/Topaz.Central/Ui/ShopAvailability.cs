using Dapper;
using Npgsql;

namespace Topaz.Central.Ui;

/// <summary>
/// Склад → Товары: how well the shops kept their assortment on the shelf over a period.
/// "Доступность ядра" — the share of days the core items (marked "always in stock", else the A class by revenue) were
/// there; "Упущенная выручка" — what the items would have sold on the days they were out: the demand per day on the days
/// they were in stock (as in the purchasing plan) × the average selling price × the days out of stock.
/// </summary>
public static class ShopAvailability
{
    public sealed class Item
    {
        public Guid StationId { get; init; }
        public string Station { get; init; } = "";
        public long ItemId { get; init; }
        public string ItemKey { get; init; } = "";
        public string? Name { get; init; }
        public string? GroupName { get; init; }
        public string Mode { get; init; } = "auto";
        public decimal Stock { get; init; }
        public decimal SoldInStock { get; init; }
        public int InStockDays { get; init; }
        public decimal LongSoldInStock { get; init; }
        public int LongInStockDays { get; init; }
        /// <summary>All days of the long window since the item first appeared.</summary>
        public int LongDays { get; init; }
        public decimal Revenue { get; init; }
        public decimal SoldAmount { get; init; }
        public decimal SoldQty { get; init; }
        public decimal? RetailPrice { get; init; }
        public int PeriodDays { get; init; }
        public int OutDays { get; init; }
    }

    public sealed record ItemLoss(Guid StationId, string Station, string ItemKey, string? Name, string? GroupName, bool Core,
        decimal Stock, int PeriodDays, int OutDays, decimal PerDay, decimal? Price, decimal LostQty, decimal? LostRevenue);

    public sealed record StationFigures(Guid StationId, string Station, int CoreItems, bool CoreMarked, decimal? Availability,
        int CoreOutNow, int OutItems, decimal LostRevenue);

    /// <summary>Fewer days on the shelf than this over the long window: the demand is not estimated from those days alone.</summary>
    public const int MinLongInStockDays = 14;

    /// <summary>
    /// Demand per day on the days in stock, as in the purchasing plan (the long window when the item was hardly on the shelf
    /// lately). An item that was on the shelf only a few days in half a year (a one-off batch sold out in two days) gets the
    /// average over all its days instead, so a single burst does not turn into months of "lost" sales.
    /// </summary>
    public static decimal PerDay(Item i)
    {
        if (i.InStockDays >= PurchasePlanner.MinInStockDays) return i.SoldInStock / i.InStockDays;
        if (i.LongInStockDays >= MinLongInStockDays) return i.LongSoldInStock / i.LongInStockDays;
        return i.LongDays > 0 ? i.LongSoldInStock / i.LongDays : 0;
    }

    /// <summary>Lost sales of every item, and per station: core availability (core = marked, else class A), lost revenue.</summary>
    public static (List<StationFigures> Stations, List<ItemLoss> Items) Compute(IReadOnlyList<Item> items)
    {
        var losses = new List<ItemLoss>();
        var stations = new List<StationFigures>();
        foreach (var g in items.GroupBy(i => (i.StationId, i.Station)))
        {
            var marked = g.Any(i => i.Mode == "core");
            var abc = Forecast.Abc(g.Select(i => (i.ItemKey, i.Revenue)));
            bool IsCore(Item i) => marked ? i.Mode == "core" : i.Revenue > 0 && abc.GetValueOrDefault(i.ItemKey) == "A";
            var rows = g.Select(i =>
            {
                var perDay = PerDay(i);
                var price = i.SoldQty > 0 ? i.SoldAmount / i.SoldQty : i.RetailPrice;
                var lostQty = Math.Round(perDay * i.OutDays, 2);
                return new ItemLoss(i.StationId, i.Station, i.ItemKey, i.Name, i.GroupName, IsCore(i), i.Stock, i.PeriodDays, i.OutDays,
                    Math.Round(perDay, 3), price is { } p ? Math.Round(p, 2) : null, lostQty, price is { } q ? Math.Round(q * perDay * i.OutDays, 2) : null);
            }).ToList();
            losses.AddRange(rows);
            var core = rows.Where(r => r.Core && r.PeriodDays > 0).ToList();
            var days = core.Sum(r => r.PeriodDays);
            stations.Add(new StationFigures(g.Key.StationId, g.Key.Station, core.Count, marked,
                days > 0 ? Math.Round(1m - (decimal)core.Sum(r => r.OutDays) / days, 4) : null,
                rows.Count(r => r.Core && r.Stock <= 0), rows.Count(r => r.OutDays > 0 && r.PerDay > 0),
                rows.Sum(r => r.LostRevenue ?? 0)));
        }
        return (stations.OrderBy(s => s.Station).ToList(),
                losses.Where(l => l.LostRevenue > 0 || (l.LostRevenue is null && l.LostQty > 0)).OrderByDescending(l => l.LostRevenue ?? 0).ToList());
    }

    /// <summary>
    /// Items sold in the <see cref="PurchasePlanner.LongDays"/> days up to the end of the period (or marked "always in stock"),
    /// not marked "do not buy", with their days in and out of stock within [from, to] (up to yesterday), counted from the
    /// item's first stock move. Demand windows end with the period: the last <paramref name="historyDays"/> days and the long one.
    /// </summary>
    public static async Task<List<Item>> LoadAsync(NpgsqlConnection c, Guid[] stations, DateTime from, DateTime to, int historyDays) => (await c.QueryAsync<Item>("""
        WITH win AS (
            SELECT s.station_id, sn.station, sn.time_zone, @from::date AS p_from,
                   LEAST(@to::date, (now() AT TIME ZONE sn.time_zone)::date - 1) AS p_to
            FROM unnest(@stations::uuid[]) s(station_id) JOIN v_station_names sn ON sn.station_id = s.station_id),
        w2 AS (SELECT *, LEAST(p_from, p_to - @longDays + 1) AS d0, p_to - @days + 1 AS dr FROM win WHERE p_to >= p_from),
        mv AS (
            SELECT m.station_id, m.item_id, (m.moved_at AT TIME ZONE w.time_zone)::date AS day, sum(m.quantity) AS q
            FROM item_moves m JOIN w2 w ON w.station_id = m.station_id
            WHERE m.item_id IS NOT NULL GROUP BY 1, 2, 3),
        base AS (
            SELECT mv.station_id, mv.item_id, COALESCE(sum(mv.q) FILTER (WHERE mv.day < w.d0), 0) AS start_stock,
                   sum(mv.q) AS stock_now, min(mv.day) AS first_day
            FROM mv JOIN w2 w ON w.station_id = mv.station_id
            GROUP BY 1, 2 HAVING sum(mv.q) < @placeholder),
        sales AS (
            SELECT a.station_id, a.item_id, a.local_date AS day, sum(a.quantity) AS q, sum(a.amount) AS amount
            FROM agg_shop_daily a JOIN w2 w ON w.station_id = a.station_id
            WHERE a.local_date BETWEEN w.d0 AND w.p_to GROUP BY 1, 2, 3),
        grid AS (
            SELECT b.station_id, b.item_id, d.day, d.day >= w.dr AS recent, d.day >= w.p_from AS in_period, d.day > w.p_to - @longDays AS in_long,
                   COALESCE(s.q, 0) AS sold, COALESCE(s.amount, 0) AS amount,
                   b.start_stock + COALESCE(sum(mv.q) OVER (PARTITION BY b.station_id, b.item_id ORDER BY d.day
                                                          ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING), 0) AS stock_start
            FROM base b
            JOIN w2 w ON w.station_id = b.station_id
            CROSS JOIN LATERAL generate_series(GREATEST(w.d0, b.first_day), w.p_to, interval '1 day') g(ts)
            CROSS JOIN LATERAL (SELECT g.ts::date AS day) d
            LEFT JOIN mv ON mv.station_id = b.station_id AND mv.item_id = b.item_id AND mv.day = d.day
            LEFT JOIN sales s ON s.station_id = b.station_id AND s.item_id = b.item_id AND s.day = d.day),
        hist AS (
            SELECT station_id, item_id,
                   COALESCE(sum(sold) FILTER (WHERE recent AND (stock_start > 0 OR sold > 0)), 0) AS sold_in_stock,
                   count(*) FILTER (WHERE recent AND (stock_start > 0 OR sold > 0)) AS in_stock_days,
                   COALESCE(sum(sold) FILTER (WHERE in_long AND (stock_start > 0 OR sold > 0)), 0) AS long_sold_in_stock,
                   count(*) FILTER (WHERE in_long AND (stock_start > 0 OR sold > 0)) AS long_in_stock_days,
                   count(*) FILTER (WHERE in_long) AS long_days,
                   COALESCE(sum(amount) FILTER (WHERE recent), 0) AS revenue,
                   COALESCE(sum(amount) FILTER (WHERE sold > 0), 0) AS sold_amount,
                   COALESCE(sum(sold) FILTER (WHERE sold > 0), 0) AS sold_qty,
                   count(*) FILTER (WHERE in_period) AS period_days,
                   count(*) FILTER (WHERE in_period AND NOT (stock_start > 0 OR sold > 0)) AS out_days
            FROM grid GROUP BY 1, 2)
        SELECT b.station_id AS StationId, w.station AS Station, b.item_id AS ItemId, k.item_key AS ItemKey, k.name AS Name, k.group_name AS GroupName,
               COALESCE(ip.mode, 'auto') AS Mode, b.stock_now AS Stock,
               h.sold_in_stock AS SoldInStock, h.in_stock_days::int AS InStockDays, h.long_sold_in_stock AS LongSoldInStock,
               h.long_in_stock_days::int AS LongInStockDays, h.long_days::int AS LongDays, h.revenue AS Revenue, h.sold_amount AS SoldAmount, h.sold_qty AS SoldQty,
               (SELECT (p.data->>'Value')::numeric FROM records p WHERE p.station_id = b.station_id AND p.dataset = 'dcItemPrices'
                 AND p.data->>'ItemID' = b.item_id::text ORDER BY (p.data->>'PriceID')::int DESC LIMIT 1) AS RetailPrice,
               h.period_days::int AS PeriodDays, h.out_days::int AS OutDays
        FROM base b
        JOIN w2 w ON w.station_id = b.station_id
        JOIN hist h ON h.station_id = b.station_id AND h.item_id = b.item_id
        JOIN v_item_keys k ON k.station_id = b.station_id AND k.item_id = b.item_id
        LEFT JOIN item_plan ip ON ip.station_id = b.station_id AND ip.item_key = k.item_key
        WHERE COALESCE(ip.mode, 'auto') <> 'stop' AND (h.long_sold_in_stock > 0 OR ip.mode = 'core')
        """, new { stations, from, to, days = historyDays, longDays = PurchasePlanner.LongDays, placeholder = SalesAnalyticsEndpoints.PlaceholderStock },
        commandTimeout: 120)).ToList();
}
