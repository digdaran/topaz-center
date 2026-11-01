using Dapper;
using Npgsql;

namespace Topaz.Central.Processing;

/// <summary>
/// Keeps the daily rollups (agg_*) in sync: takes the days marked in rollup_dirty and recomputes them
/// from the raw tables.
/// Woken after each processed batch and on station time zone changes; polls every 5 s as a fallback.
/// Lag: seconds after a batch is processed (UI reads rollups only for day/week/month figures).
/// </summary>
public sealed class RollupRefresher(NpgsqlDataSource db, ILogger<RollupRefresher> log) : BackgroundService
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        try { if (_signal.CurrentCount == 0) _signal.Release(); } catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastFull = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Safety net (e.g. an order whose date changed): rebuild everything once a day.
                if (DateTime.UtcNow - lastFull > TimeSpan.FromHours(24))
                {
                    await using var c = await db.OpenConnectionAsync(stoppingToken);
                    await c.ExecuteAsync("INSERT INTO rollup_dirty (station_id, kind) SELECT id, 'all' FROM stations");
                    lastFull = DateTime.UtcNow;
                }
                while (await RefreshAsync(stoppingToken) > 0) { }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Rollup refresh failed"); }
            try { await _signal.WaitAsync(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private sealed record Dirty(long Id, Guid StationId, string Kind, DateTime? UtcDate);

    /// <summary>Processes one station's dirty marks. Returns how many marks were handled.</summary>
    public async Task<int> RefreshAsync(CancellationToken ct)
    {
        await using var c = await db.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var station = await c.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT station_id FROM rollup_dirty ORDER BY id LIMIT 1 FOR UPDATE SKIP LOCKED", transaction: tx, cancellationToken: ct));
        if (station is null) return 0;
        // Lock and take every mark of this station (new marks arriving meanwhile stay for the next round).
        var marks = (await c.QueryAsync<Dirty>(new CommandDefinition(
            "SELECT id, station_id AS StationId, kind, utc_date AS UtcDate FROM rollup_dirty WHERE station_id = @s FOR UPDATE",
            new { s = station }, tx, cancellationToken: ct))).ToList();

        var all = marks.Any(m => m.Kind == "all");
        // A UTC day can be a neighbouring local day in any time zone (offsets are within ±14 h).
        DateTime[] Days(string kind) => marks.Where(m => m.Kind == kind && m.UtcDate is not null)
            .SelectMany(m => new[] { m.UtcDate!.Value.AddDays(-1), m.UtcDate.Value, m.UtcDate.Value.AddDays(1) }).Distinct().ToArray();
        var fuelDays = all ? null : Days("fuel");
        var shopDays = all ? null : Days("shop");

        if (all || fuelDays!.Length > 0) await RebuildFuel(c, tx, station.Value, fuelDays, ct);
        if (all || shopDays!.Length > 0) await RebuildShop(c, tx, station.Value, shopDays, ct);
        await c.ExecuteAsync(new CommandDefinition("DELETE FROM rollup_dirty WHERE id = ANY(@ids)", new { ids = marks.Select(m => m.Id).ToArray() }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return marks.Count;
    }

    private static DateTime? Utc(DateTime? d) => d is null ? null : DateTime.SpecifyKind(d.Value, DateTimeKind.Utc);

    private static async Task RebuildFuel(NpgsqlConnection c, NpgsqlTransaction tx, Guid s, DateTime[]? days, CancellationToken ct)
    {
        var where = days is null ? "station_id = @s" : "station_id = @s AND local_date = ANY(@d::date[])";
        var bound = days is null ? "" : " AND sold_at >= @lo AND sold_at < @hi";
        var args = new { s, d = days, lo = Utc(days?.Min().AddDays(-2)), hi = Utc(days?.Max().AddDays(3)) };
        await c.ExecuteAsync(new CommandDefinition($"DELETE FROM agg_fuel_daily WHERE {where}", args, tx, cancellationToken: ct, commandTimeout: 600));
        await c.ExecuteAsync(new CommandDefinition($"""
            INSERT INTO agg_fuel_daily (station_id, local_date, fuel_id, payment_mode_id, payment_kind, revenue, liters, orders, aborted,
                                        service_liters, discounts, last_price, last_sold_at, cost_amount, costed_liters, costed_revenue, service_cost)
            SELECT s.station_id, s.local_date, COALESCE(s.fuel_id, 0), COALESCE(s.payment_mode_id, 0), s.payment_kind,
                   COALESCE(sum(s.amount), 0), COALESCE(sum(s.volume) FILTER (WHERE s.is_sale), 0), count(*) FILTER (WHERE s.is_sale),
                   count(*) FILTER (WHERE s.volume = 0), COALESCE(sum(s.volume) FILTER (WHERE s.payment_kind = 'none' AND s.volume > 0), 0),
                   COALESCE(sum(s.amount_discount), 0), (array_agg(s.price ORDER BY s.sold_at DESC) FILTER (WHERE s.is_sale))[1], max(s.sold_at),
                   sum(s.volume * c.price) FILTER (WHERE s.is_sale AND c.price IS NOT NULL),
                   COALESCE(sum(s.volume) FILTER (WHERE s.is_sale AND c.price IS NOT NULL), 0),
                   COALESCE(sum(s.amount) FILTER (WHERE s.is_sale AND c.price IS NOT NULL), 0),
                   sum(s.volume * c.price) FILTER (WHERE s.payment_kind = 'none' AND s.volume > 0 AND c.price IS NOT NULL)
            FROM v_fuel_sales_local s
            -- purchase price in effect at the moment of the sale
            LEFT JOIN LATERAL (
                SELECT fc.price_per_liter AS price FROM fuel_costs fc
                WHERE fc.station_id = s.station_id AND fc.fuel_id = s.fuel_id AND fc.valid_from <= s.sold_at
                ORDER BY fc.valid_from DESC LIMIT 1) c ON true
            WHERE {where.Replace("station_id", "s.station_id").Replace("local_date", "s.local_date")}{bound.Replace("sold_at", "s.sold_at")} AND s.local_date IS NOT NULL
            GROUP BY 1, 2, 3, 4, 5
            """, args, tx, cancellationToken: ct, commandTimeout: 600));
    }

    private static async Task RebuildShop(NpgsqlConnection c, NpgsqlTransaction tx, Guid s, DateTime[]? days, CancellationToken ct)
    {
        var where = days is null ? "station_id = @s" : "station_id = @s AND local_date = ANY(@d::date[])";
        var bound = days is null ? "" : " AND doc_at >= @lo AND doc_at < @hi";
        var args = new { s, d = days, lo = Utc(days?.Min().AddDays(-2)), hi = Utc(days?.Max().AddDays(3)) };
        await c.ExecuteAsync(new CommandDefinition($"DELETE FROM agg_shop_daily WHERE {where}", args, tx, cancellationToken: ct, commandTimeout: 600));
        await c.ExecuteAsync(new CommandDefinition($"DELETE FROM agg_shop_receipts_daily WHERE {where}", args, tx, cancellationToken: ct, commandTimeout: 600));
        await c.ExecuteAsync(new CommandDefinition($"""
            INSERT INTO agg_shop_daily (station_id, local_date, item_id, payment_mode_id, quantity, amount, lines, receipts, cost_amount, costed_amount)
            SELECT l.station_id, l.local_date, COALESCE(l.item_id, 0), COALESCE(l.payment_mode_id, 0), COALESCE(sum(l.quantity), 0), COALESCE(sum(l.amount), 0),
                   count(*), count(DISTINCT l.receipt_id),
                   sum(l.quantity * c.cost) FILTER (WHERE c.cost IS NOT NULL),
                   COALESCE(sum(l.amount) FILTER (WHERE c.cost IS NOT NULL), 0)
            FROM v_shop_lines_local l
            LEFT JOIN v_item_keys k ON k.station_id = l.station_id AND k.item_id = l.item_id
            -- latest cost in effect at the sale; a station-specific price wins over an "all stations" one of the same date
            LEFT JOIN LATERAL (
                SELECT ic.cost FROM item_costs ic
                WHERE ic.item_key = k.item_key AND ic.valid_from <= l.doc_at
                  AND (ic.station_id = l.station_id OR (ic.station_id IS NULL AND ic.organization_id = (SELECT organization_id FROM stations WHERE id = l.station_id)))
                ORDER BY ic.valid_from DESC, (ic.station_id IS NULL) LIMIT 1) c ON true
            WHERE {where.Replace("station_id", "l.station_id").Replace("local_date", "l.local_date")}{bound.Replace("doc_at", "l.doc_at")} AND l.local_date IS NOT NULL
            GROUP BY 1, 2, 3, 4
            """, args, tx, cancellationToken: ct, commandTimeout: 600));
        await c.ExecuteAsync(new CommandDefinition($"""
            INSERT INTO agg_shop_receipts_daily (station_id, local_date, receipts, amount)
            SELECT station_id, local_date, count(DISTINCT receipt_id), COALESCE(sum(amount), 0)
            FROM v_shop_lines_local WHERE {where}{bound} AND local_date IS NOT NULL
            GROUP BY 1, 2
            """, args, tx, cancellationToken: ct, commandTimeout: 600));
    }
}
