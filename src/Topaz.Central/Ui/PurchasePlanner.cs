using Dapper;
using Npgsql;

namespace Topaz.Central.Ui;

/// <summary>
/// Shop purchasing (1.23): how much of each item to buy on the next trip so it lasts until the trip after it.
/// Demand per day is counted only over the days the item was in stock (a day on zero sells nothing, and counting it
/// would lower the order of exactly the items that run out); safety stock = z(service level of the item's ABC class)
/// × σ of daily sales × √horizon; the quantity is rounded up to the pack size.
/// </summary>
public static class PurchasePlanner
{
    public sealed record Settings(int[] TripWeekdays, int LeadDays, decimal ServiceA, decimal ServiceB, decimal ServiceC, int HistoryDays)
    {
        public static readonly Settings Default = new([1, 2, 3, 4, 5, 6], 0, 97, 93, 85, 56);
        public decimal Service(string abc) => abc switch { "A" => ServiceA, "B" => ServiceB, _ => ServiceC };
    }

    /// <summary>One item at one station: its history over the planning window and the owner's plan.</summary>
    public sealed class Item
    {
        public Guid StationId { get; set; }
        public string Station { get; set; } = "";
        public long ItemId { get; set; }
        public string ItemKey { get; set; } = "";
        public string? Name { get; set; }
        public string? GroupName { get; set; }
        public decimal Stock { get; set; }
        /// <summary>Σ sales on the days the item was in stock (stock at the start of the day &gt; 0, or it sold).</summary>
        public decimal SoldInStock { get; set; }
        public decimal SoldSquares { get; set; }
        public int InStockDays { get; set; }
        public int ZeroDays { get; set; }
        /// <summary>The same over the long window (<see cref="LongDays"/>): for items that have hardly been on the shelf lately.</summary>
        public decimal LongSoldInStock { get; set; }
        public decimal LongSoldSquares { get; set; }
        public int LongInStockDays { get; set; }
        public decimal Revenue { get; set; }
        public DateTime? LastSale { get; set; }
        public bool IsWeight { get; set; }
        public decimal? RetailPrice { get; set; }
        public decimal? LastBuyPrice { get; set; }
        public decimal? CostPrice { get; set; }
        public decimal? PackGuess { get; set; }
        public decimal OnOrder { get; set; }
        public string Mode { get; set; } = "auto";
        public decimal? MinQty { get; set; }
        public decimal? MaxQty { get; set; }
        public decimal? PackQty { get; set; }
        public string? Note { get; set; }
    }

    public sealed record Line(
        Guid StationId, string Station, long ItemId, string ItemKey, string? Name, string? GroupName, string Mode, string Abc,
        decimal Stock, decimal OnOrder, decimal PerDay, decimal? DaysOfCover, int InStockDays, int ZeroDays, bool LowData,
        decimal Safety, decimal Target, decimal Pack, bool PackLearned, decimal Qty, decimal? Price, decimal? Amount, string Status,
        decimal? MinQty, decimal? MaxQty, string? Note, DateTime? LastSale);

    /// <summary>History for items that have hardly been on the shelf within the planning window.</summary>
    public const int LongDays = 180;
    private const int MinInStockDays = 7;

    /// <summary>The next trip (today if today is a trip day) and the one after it.</summary>
    public static (DateTime Next, DateTime Following) Trips(DateTime today, IReadOnlyCollection<int> weekdays)
    {
        static int Iso(DateTime d) => d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek;
        var days = weekdays.Count > 0 ? weekdays : [1, 2, 3, 4, 5, 6, 7];
        var next = today.Date;
        while (!days.Contains(Iso(next))) next = next.AddDays(1);
        var following = next.AddDays(1);
        while (!days.Contains(Iso(following))) following = following.AddDays(1);
        return (next, following);
    }

    /// <summary>Quantile of the standard normal distribution for a service level in percent (Acklam's approximation).</summary>
    public static decimal Z(decimal servicePercent)
    {
        var p = Math.Clamp((double)servicePercent / 100.0, 0.5, 0.999);
        double[] a = [-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00];
        double[] b = [-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01];
        double[] c = [-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00];
        double[] d = [7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00];
        double x;
        if (p > 1 - 0.02425)
        {
            var q = Math.Sqrt(-2 * Math.Log(1 - p));
            x = -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        else
        {
            var q = p - 0.5;
            var r = q * q;
            x = (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
        }
        return Math.Round((decimal)x, 3);
    }

    /// <summary>Rounds a need up to whole packs (weight goods without a pack: to 0.1).</summary>
    public static decimal RoundUp(decimal need, decimal pack) =>
        need <= 0 ? 0 : Math.Ceiling(need / pack) * pack;

    /// <summary>
    /// The quantity to buy so the item lasts <paramref name="horizonDays"/> days (until the following trip plus the
    /// lead time), never below the owner's minimum (a "core" item: at least one pack), never above the maximum.
    /// </summary>
    public static Line Compute(Item i, string abc, Settings s, int horizonDays)
    {
        // Hardly on the shelf lately (sold out long ago): the demand from the days it was there over the long window.
        var useLong = i.InStockDays < MinInStockDays && i.LongInStockDays > i.InStockDays;
        var (sold, squares, days) = useLong ? (i.LongSoldInStock, i.LongSoldSquares, i.LongInStockDays) : (i.SoldInStock, i.SoldSquares, i.InStockDays);
        var perDay = days > 0 ? sold / days : 0;
        var variance = days > 0 ? squares / days - perDay * perDay : 0;
        var sigma = (decimal)Math.Sqrt((double)Math.Max(0, variance));
        var pack = i.PackQty ?? i.PackGuess ?? (i.IsWeight ? 0.1m : 1m);
        var stock = Math.Max(0, i.Stock);
        var safety = perDay > 0 ? Math.Round(Z(s.Service(abc)) * sigma * (decimal)Math.Sqrt(horizonDays), 2) : 0;
        var target = perDay * horizonDays + safety;
        if (i.Mode == "core") target = Math.Max(target, pack);
        if (i.MinQty is { } min) target = Math.Max(target, min);
        if (i.MaxQty is { } max) target = Math.Min(target, max);
        var qty = i.Mode == "stop" ? 0 : RoundUp(target - stock - i.OnOrder, pack);
        if (i.MaxQty is { } cap && qty > 0 && stock + i.OnOrder + qty > cap)
            qty = Math.Max(0, Math.Floor((cap - stock - i.OnOrder) / pack) * pack);   // packs that still fit under the maximum
        var price = i.LastBuyPrice ?? i.CostPrice;
        var cover = perDay > 0 ? Math.Round(stock / perDay, 1) : (decimal?)null;
        var status = i.Mode == "stop" ? "stop"
            : stock <= 0 && (perDay > 0 || i.Mode == "core") ? "out"
            : qty > 0 && cover is { } c && c < horizonDays ? "low"
            : qty > 0 ? "refill" : "ok";
        return new Line(i.StationId, i.Station, i.ItemId, i.ItemKey, i.Name, i.GroupName, i.Mode, abc,
            i.Stock, i.OnOrder, Math.Round(perDay, 3), cover, days, i.ZeroDays, days < 14,
            safety, Math.Round(target, 2), pack, i.PackQty is null && i.PackGuess is not null, qty, price,
            price is { } p ? Math.Round(p * qty, 2) : null, status, i.MinQty, i.MaxQty, i.Note, i.LastSale);
    }

    /// <summary>
    /// Items of the stations with their history over the last <c>HistoryDays</c> full days (and <see cref="LongDays"/> for
    /// items sold out long ago: they stay in the plan until the owner buys them again or marks them "do not buy").
    /// </summary>
    public static async Task<List<Item>> LoadAsync(NpgsqlConnection c, Guid[] stations, int historyDays) => (await c.QueryAsync<Item>("""
        WITH win AS (
            SELECT s.station_id, sn.station, sn.time_zone, (now() AT TIME ZONE sn.time_zone)::date AS today,
                   (now() AT TIME ZONE sn.time_zone)::date - GREATEST(@days, @longDays) AS d0,
                   (now() AT TIME ZONE sn.time_zone)::date - @days AS d1
            FROM unnest(@stations::uuid[]) s(station_id) JOIN v_station_names sn ON sn.station_id = s.station_id),
        mv AS (
            SELECT m.station_id, m.item_id, (m.moved_at AT TIME ZONE w.time_zone)::date AS day, sum(m.quantity) AS q
            FROM item_moves m JOIN win w ON w.station_id = m.station_id
            WHERE m.item_id IS NOT NULL GROUP BY 1, 2, 3),
        -- items with stock history, plus the owner's "always in stock" / minimum items that have never been there
        base AS (
            SELECT c.station_id, c.item_id, COALESCE(sum(mv.q) FILTER (WHERE mv.day < w.d0), 0) AS start_stock, COALESCE(sum(mv.q), 0) AS stock_now
            FROM (SELECT station_id, item_id FROM mv
                  UNION
                  SELECT ip.station_id, k.item_id FROM item_plan ip
                  JOIN v_item_keys k ON k.station_id = ip.station_id AND k.item_key = ip.item_key
                  WHERE ip.station_id = ANY(@stations::uuid[]) AND (ip.mode = 'core' OR ip.min_qty > 0)) c
            JOIN win w ON w.station_id = c.station_id
            LEFT JOIN mv ON mv.station_id = c.station_id AND mv.item_id = c.item_id
            GROUP BY 1, 2),
        sales AS (
            SELECT a.station_id, a.item_id, a.local_date AS day, sum(a.quantity) AS q, sum(a.amount) AS amount
            FROM agg_shop_daily a JOIN win w ON w.station_id = a.station_id
            WHERE a.local_date >= w.d0 AND a.local_date < w.today GROUP BY 1, 2, 3),
        grid AS (
            SELECT b.station_id, b.item_id, d.day, d.day >= w.d1 AS recent, COALESCE(s.q, 0) AS sold, COALESCE(s.amount, 0) AS amount,
                   b.start_stock + COALESCE(sum(mv.q) OVER (PARTITION BY b.station_id, b.item_id ORDER BY d.day
                                                          ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING), 0) AS stock_start
            FROM base b
            JOIN win w ON w.station_id = b.station_id
            CROSS JOIN LATERAL generate_series(w.d0, w.today - 1, interval '1 day') g(ts)
            CROSS JOIN LATERAL (SELECT g.ts::date AS day) d
            LEFT JOIN mv ON mv.station_id = b.station_id AND mv.item_id = b.item_id AND mv.day = d.day
            LEFT JOIN sales s ON s.station_id = b.station_id AND s.item_id = b.item_id AND s.day = d.day
            WHERE b.stock_now < @placeholder),
        hist AS (
            SELECT station_id, item_id,
                   COALESCE(sum(sold) FILTER (WHERE recent AND (stock_start > 0 OR sold > 0)), 0) AS sold_in_stock,
                   COALESCE(sum(sold * sold) FILTER (WHERE recent AND (stock_start > 0 OR sold > 0)), 0) AS sold_squares,
                   count(*) FILTER (WHERE recent AND (stock_start > 0 OR sold > 0)) AS in_stock_days,
                   count(*) FILTER (WHERE recent AND NOT (stock_start > 0 OR sold > 0)) AS zero_days,
                   COALESCE(sum(sold) FILTER (WHERE stock_start > 0 OR sold > 0), 0) AS long_sold_in_stock,
                   COALESCE(sum(sold * sold) FILTER (WHERE stock_start > 0 OR sold > 0), 0) AS long_sold_squares,
                   count(*) FILTER (WHERE stock_start > 0 OR sold > 0) AS long_in_stock_days,
                   COALESCE(sum(amount) FILTER (WHERE recent), 0) AS revenue,
                   max(day) FILTER (WHERE sold > 0) AS last_sale,
                   bool_or(sold <> trunc(sold)) AS is_weight
            FROM grid GROUP BY 1, 2)
        SELECT b.station_id AS StationId, w.station AS Station, b.item_id AS ItemId, k.item_key AS ItemKey, k.name AS Name, k.group_name AS GroupName,
               b.stock_now AS Stock, h.sold_in_stock AS SoldInStock, h.sold_squares AS SoldSquares, h.in_stock_days::int AS InStockDays,
               h.zero_days::int AS ZeroDays, h.long_sold_in_stock AS LongSoldInStock, h.long_sold_squares AS LongSoldSquares,
               h.long_in_stock_days::int AS LongInStockDays, h.revenue AS Revenue, h.last_sale::timestamp AS LastSale, COALESCE(h.is_weight, false) AS IsWeight,
               (SELECT (p.data->>'Value')::numeric FROM records p WHERE p.station_id = b.station_id AND p.dataset = 'dcItemPrices'
                 AND p.data->>'ItemID' = b.item_id::text ORDER BY (p.data->>'PriceID')::int DESC LIMIT 1) AS RetailPrice,
               (SELECT bl.price FROM purchase_bill_lines bl JOIN purchase_bills pb ON pb.station_id = bl.station_id AND pb.bill_id = bl.bill_id
                 WHERE bl.station_id = b.station_id AND bl.item_id = b.item_id AND bl.price > 0 AND NOT COALESCE(pb.is_return, false)
                 ORDER BY pb.doc_at DESC NULLS LAST, bl.line_id DESC LIMIT 1) AS LastBuyPrice,
               (SELECT ic.cost FROM item_costs ic
                 WHERE ic.item_key = k.item_key AND ic.valid_from <= now()
                   AND (ic.station_id = b.station_id OR (ic.station_id IS NULL AND ic.organization_id = (SELECT organization_id FROM stations WHERE id = b.station_id)))
                 ORDER BY ic.valid_from DESC, (ic.station_id IS NULL) LIMIT 1) AS CostPrice,
               -- the usual pack: the quantity bought most often, if it was bought at least twice
               (SELECT x.quantity FROM (SELECT bl.quantity, count(*) AS n FROM purchase_bill_lines bl
                                        WHERE bl.station_id = b.station_id AND bl.item_id = b.item_id AND bl.quantity > 0
                                        GROUP BY 1) x WHERE x.n >= 2 ORDER BY x.n DESC, x.quantity LIMIT 1) AS PackGuess,
               COALESCE((SELECT sum(o.open_qty) FROM v_purchase_on_order o WHERE o.station_id = b.station_id AND o.item_id = b.item_id
                          AND o.bought_at > now() - interval '14 days'), 0) AS OnOrder,
               COALESCE(ip.mode, 'auto') AS Mode, ip.min_qty AS MinQty, ip.max_qty AS MaxQty, ip.pack_qty AS PackQty, ip.note AS Note
        FROM base b
        JOIN win w ON w.station_id = b.station_id
        JOIN v_item_keys k ON k.station_id = b.station_id AND k.item_id = b.item_id
        LEFT JOIN hist h ON h.station_id = b.station_id AND h.item_id = b.item_id
        LEFT JOIN item_plan ip ON ip.station_id = b.station_id AND ip.item_key = k.item_key
        WHERE b.stock_now < @placeholder AND (h.long_sold_in_stock > 0 OR ip.mode = 'core' OR ip.min_qty > 0)
        """, new { stations, days = historyDays, longDays = LongDays, placeholder = SalesAnalyticsEndpoints.PlaceholderStock }, commandTimeout: 120)).ToList();

    /// <summary>Lines for the next trip: ABC by revenue within each station, then the quantity of every item.</summary>
    public static List<Line> Plan(IReadOnlyList<Item> items, Settings s, int horizonDays) =>
        items.GroupBy(i => i.StationId).SelectMany(g =>
        {
            var abc = Forecast.Abc(g.Select(i => (i.ItemKey, i.Revenue)));
            return g.Select(i => Compute(i, abc.GetValueOrDefault(i.ItemKey, "C"), s, horizonDays));
        }).ToList();
}
