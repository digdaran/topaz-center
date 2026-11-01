using Dapper;
using Npgsql;

namespace Topaz.Central.Processing;

/// <summary>
/// Cost of a litre per tank as a moving average (fuel mixes in the tank): at a priced receipt
/// avg := (stock before × avg + litres × price) / (stock before + litres). Stock before comes from Topaz's
/// book (shift start + earlier receipts − dispensing of that shift). A receipt without a price keeps the
/// average; the first priced receipt values the stock already in the tank at its own price; a "stock price
/// on a date" (fuel_costs without receipt) sets the average of every tank of that fuel.
/// Writes fuel_cost_avg for one station and returns the earliest moment whose average changed.
/// </summary>
public static class FuelCostAverager
{
    private sealed record Receipt(long ReceiptId, long TankId, long FuelId, DateTime ReceivedAt, decimal Liters, decimal? Price, decimal StockBefore);
    private sealed record StockPrice(long FuelId, DateTime ValidFrom, decimal Price);
    public sealed record Row(long TankId, DateTime ValidFrom, int Seq, long FuelId, decimal AvgPrice, string Source,
                             long? ReceiptId, decimal? StockBefore, decimal? Liters, decimal? ReceiptPrice);

    public static async Task<DateTime?> RecomputeAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid station, CancellationToken ct)
    {
        var receipts = (await c.QueryAsync<Receipt>(new CommandDefinition("""
            SELECT r.receipt_id AS ReceiptId, r.tank_id AS TankId, r.fuel_id AS FuelId, r.received_at AS ReceivedAt, r.doc_volume AS Liters,
                   (SELECT fc.price_per_liter FROM fuel_costs fc WHERE fc.station_id = r.station_id AND fc.receipt_id = r.receipt_id
                    ORDER BY fc.created_at DESC LIMIT 1) AS Price,
                   GREATEST(0, COALESCE(t.start_volume, 0)
                       + COALESCE((SELECT sum(r2.doc_volume) FROM fuel_receipts r2
                                   WHERE r2.station_id = r.station_id AND r2.tank_id = r.tank_id AND r2.session_id = r.session_id
                                     AND (r2.received_at, r2.receipt_id) < (r.received_at, r.receipt_id)), 0)
                       - COALESCE((SELECT sum(f.volume) FROM fuel_sales f
                                   WHERE f.station_id = r.station_id AND f.session_id = r.session_id AND f.tank_id = r.tank_id
                                     AND f.volume > 0 AND f.sold_at < r.received_at), 0)) AS StockBefore
            FROM fuel_receipts r
            LEFT JOIN tank_shift_states t ON t.station_id = r.station_id AND t.session_id = r.session_id AND t.tank_id = r.tank_id
            WHERE r.station_id = @station AND r.received_at IS NOT NULL AND r.tank_id IS NOT NULL AND r.fuel_id IS NOT NULL AND r.doc_volume > 0
            """, new { station }, tx, cancellationToken: ct))).ToList();
        var stockPrices = (await c.QueryAsync<StockPrice>(new CommandDefinition("""
            SELECT fuel_id AS FuelId, valid_from AS ValidFrom, price_per_liter AS Price
            FROM fuel_costs WHERE station_id = @station AND receipt_id IS NULL
            """, new { station }, tx, cancellationToken: ct))).ToList();
        // Tanks of each fuel: the reference (dcTanks), tanks that sold it, and (below) tanks that received it.
        var tanks = (await c.QueryAsync<(long Tank, long Fuel)>(new CommandDefinition("""
            SELECT tank_id, fuel_id FROM v_tanks WHERE station_id = @station AND fuel_id IS NOT NULL
            UNION
            SELECT DISTINCT tank_id, fuel_id FROM fuel_sales WHERE station_id = @station AND tank_id IS NOT NULL AND fuel_id IS NOT NULL
            """, new { station }, tx, cancellationToken: ct))).ToList();

        var rows = Compute(receipts.Select(r => (r.ReceiptId, r.TankId, r.FuelId, Utc(r.ReceivedAt), r.Liters, r.Price, r.StockBefore)),
                           stockPrices.Select(p => (p.FuelId, Utc(p.ValidFrom), p.Price)), tanks);

        var stored = (await c.QueryAsync<Row>(new CommandDefinition("""
            SELECT tank_id AS TankId, valid_from AS ValidFrom, seq AS Seq, fuel_id AS FuelId, avg_price AS AvgPrice, source AS Source,
                   receipt_id AS ReceiptId, stock_before AS StockBefore, liters AS Liters, receipt_price AS ReceiptPrice
            FROM fuel_cost_avg WHERE station_id = @station
            """, new { station }, tx, cancellationToken: ct))).Select(r => r with { ValidFrom = Utc(r.ValidFrom) }).ToList();

        var changedAt = FirstDifference(stored, rows);
        if (changedAt is null) return null;
        await c.ExecuteAsync(new CommandDefinition("DELETE FROM fuel_cost_avg WHERE station_id = @station", new { station }, tx, cancellationToken: ct));
        if (rows.Count > 0)
            await c.ExecuteAsync(new CommandDefinition("""
                INSERT INTO fuel_cost_avg (station_id, tank_id, valid_from, seq, fuel_id, avg_price, source, receipt_id, stock_before, liters, receipt_price)
                SELECT @station, * FROM unnest(@tank, @validFrom, @seq, @fuel, @avg, @source, @receipt, @stock, @liters, @price)
                """, new
            {
                station,
                tank = rows.Select(r => r.TankId).ToArray(), validFrom = rows.Select(r => r.ValidFrom).ToArray(), seq = rows.Select(r => r.Seq).ToArray(),
                fuel = rows.Select(r => r.FuelId).ToArray(), avg = rows.Select(r => r.AvgPrice).ToArray(), source = rows.Select(r => r.Source).ToArray(),
                receipt = rows.Select(r => r.ReceiptId).ToArray(), stock = rows.Select(r => r.StockBefore).ToArray(),
                liters = rows.Select(r => r.Liters).ToArray(), price = rows.Select(r => r.ReceiptPrice).ToArray(),
            }, tx, cancellationToken: ct));
        return changedAt;
    }

    /// <summary>The averaging itself (pure, unit-tested).</summary>
    public static List<Row> Compute(IEnumerable<(long ReceiptId, long TankId, long FuelId, DateTime At, decimal Liters, decimal? Price, decimal StockBefore)> receipts,
                                    IEnumerable<(long FuelId, DateTime At, decimal Price)> stockPrices,
                                    IEnumerable<(long Tank, long Fuel)> tanks)
    {
        var receiptList = receipts.ToList();
        var tanksByFuel = tanks.Concat(receiptList.Select(r => (r.TankId, r.FuelId))).Distinct()
            .GroupBy(t => t.Item2).ToDictionary(g => g.Key, g => g.Select(t => t.Item1).Distinct().ToList());

        // Events per tank: stock prices first at the same moment, then receipts in id order.
        var events = new List<(long Tank, DateTime At, int Kind, long Order, Action<State> Apply)>();
        var rows = new List<Row>();
        foreach (var p in stockPrices)
            foreach (var tank in tanksByFuel.GetValueOrDefault(p.FuelId) ?? [])
                events.Add((tank, p.At, 0, 0, s =>
                {
                    s.Fuel = p.FuelId;
                    s.Avg = Round(p.Price);
                    rows.Add(new Row(tank, p.At, s.NextSeq(p.At), p.FuelId, s.Avg.Value, "stock_price", null, null, null, Round(p.Price)));
                }));
        foreach (var r in receiptList)
            events.Add((r.TankId, r.At, 1, r.ReceiptId, s =>
            {
                if (s.Fuel != r.FuelId) { s.Avg = null; s.Fuel = r.FuelId; } // the tank was switched to another fuel
                var stock = Math.Max(0, r.StockBefore);
                if (r.Price is { } price)
                {
                    s.Avg = s.Avg is { } avg && stock + r.Liters > 0
                        ? Round((stock * avg + r.Liters * price) / (stock + r.Liters))
                        : Round(price); // first priced receipt: the stock already there is valued at its price
                    rows.Add(new Row(r.TankId, r.At, s.NextSeq(r.At), r.FuelId, s.Avg.Value, "receipt", r.ReceiptId, stock, r.Liters, Round(price)));
                }
                else if (s.Avg is { } avg)
                    rows.Add(new Row(r.TankId, r.At, s.NextSeq(r.At), r.FuelId, avg, "receipt_unpriced", r.ReceiptId, stock, r.Liters, null));
            }));

        var states = new Dictionary<long, State>();
        foreach (var e in events.OrderBy(e => e.Tank).ThenBy(e => e.At).ThenBy(e => e.Kind).ThenBy(e => e.Order))
        {
            if (!states.TryGetValue(e.Tank, out var s)) states[e.Tank] = s = new State();
            e.Apply(s);
        }
        return rows;
    }

    private sealed class State
    {
        public decimal? Avg;
        public long? Fuel;
        private DateTime _at;
        private int _seq;
        public int NextSeq(DateTime at)
        {
            if (at != _at) { _at = at; _seq = 0; }
            return _seq++;
        }
    }

    private static decimal Round(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);
    private static DateTime Utc(DateTime d) => d.Kind == DateTimeKind.Utc ? d : DateTime.SpecifyKind(d.ToUniversalTime(), DateTimeKind.Utc);

    /// <summary>Earliest moment at which the stored and the new averages differ (null: identical).</summary>
    private static DateTime? FirstDifference(List<Row> stored, List<Row> fresh)
    {
        static string K(Row r) => $"{r.TankId}|{r.ValidFrom.Ticks}|{r.Seq}";
        var a = stored.ToDictionary(K);
        var b = fresh.ToDictionary(K);
        DateTime? first = null;
        void Mark(DateTime t) { if (first is null || t < first) first = t; }
        foreach (var (k, r) in b)
            if (!a.TryGetValue(k, out var o) || o.AvgPrice != r.AvgPrice || o.Source != r.Source || o.ReceiptId != r.ReceiptId
                || o.StockBefore != r.StockBefore || o.Liters != r.Liters || o.ReceiptPrice != r.ReceiptPrice || o.FuelId != r.FuelId)
                Mark(r.ValidFrom);
        foreach (var (k, r) in a)
            if (!b.ContainsKey(k)) Mark(r.ValidFrom);
        return first;
    }
}
