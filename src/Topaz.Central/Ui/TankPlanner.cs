using Dapper;
using Npgsql;

namespace Topaz.Central.Ui;

/// <summary>
/// Fuel stock per tank: book stock now, average outflow over the last 28 days, days until the minimum,
/// when to order and how much. Used by the "Запасы топлива" page and the low_fuel alert rule.
/// </summary>
public static class TankPlanner
{
    public sealed class Row
    {
        public Guid StationId { get; set; }
        public string Station { get; set; } = "";
        public long TankId { get; set; }
        public int? TankNum { get; set; }
        public string? FuelName { get; set; }
        public long SessionId { get; set; }
        public decimal BookVolume { get; set; }
        public decimal? Capacity { get; set; }
        public decimal DeadRest { get; set; }
        public decimal MinLiters { get; set; }
        public decimal LeadTimeDays { get; set; }
        public decimal SafetyDays { get; set; }
        public decimal Out28 { get; set; }
        public string TimeZone { get; set; } = "Europe/Moscow";
        /// <summary>An electronic level gauge is linked to the tank in Topaz (else: a virtual tank, book stock only).</summary>
        public bool HasGauge { get; set; }
        public decimal? LastFactVolume { get; set; }
        public DateTime? LastFactAt { get; set; }
        public int? LastFactSession { get; set; }
        public decimal? LastFactDiff { get; set; }

        public decimal PerDay => Math.Round(Out28 / 28m, 1);
        public decimal Threshold => Math.Max(MinLiters, DeadRest);
        public decimal? FillShare => Capacity is > 0 ? Math.Round(BookVolume / Capacity.Value, 4) : null;
        public decimal? DaysToMin => PerDay > 0 ? Math.Round(Math.Max(0, BookVolume - Threshold) / PerDay, 1) : null;
        /// <summary>Latest date to place an order so the delivery arrives before the minimum (lead time + safety).</summary>
        public DateTime? OrderBy => DaysToMin is { } d ? Today.AddDays((double)Math.Floor(d - LeadTimeDays - SafetyDays)) : null;
        /// <summary>Fill up to 95 % of the capacity, counting what is sold until the truck arrives; rounded to 100 l.</summary>
        public decimal RecommendedOrder => Capacity is > 0
            ? Math.Max(0, Math.Floor((Capacity.Value * 0.95m - Math.Max(0, BookVolume - PerDay * LeadTimeDays)) / 100m) * 100m) : 0;
        public string Status => BookVolume <= Threshold && PerDay > 0 ? "critical"
            : DaysToMin is { } d && d <= LeadTimeDays + SafetyDays ? "critical"
            : DaysToMin is { } d2 && d2 <= LeadTimeDays + SafetyDays + 3 ? "warning"
            : PerDay == 0 ? "idle" : "ok";
        private DateTime Today
        {
            get
            {
                try { return TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, TimeZone).Date; }
                catch (TimeZoneNotFoundException) { return DateTime.UtcNow.AddHours(3).Date; } // no tzdata: Moscow
            }
        }
    }

    public static async Task<List<Row>> ComputeAsync(NpgsqlConnection c, Guid[] stations) => (await c.QueryAsync<Row>("""
        WITH use28 AS (
            SELECT station_id, tank_id, COALESCE(sum(volume) FILTER (WHERE volume > 0), 0) AS out28
            FROM fuel_sales WHERE station_id = ANY(@stations) AND sold_at >= now() - interval '28 days' GROUP BY 1, 2)
        SELECT b.station_id AS StationId, sn.station AS Station, sn.time_zone AS TimeZone, b.tank_id AS TankId, b.tank_num AS TankNum, b.fuel_name AS FuelName,
               b.session_id AS SessionId, b.book_volume AS BookVolume,
               NULLIF((rt.data->>'MaxVolume')::numeric, 0) AS Capacity, COALESCE((rt.data->>'DeadRestLiter')::numeric, 0) AS DeadRest,
               COALESCE(ts.min_liters, 0) AS MinLiters, COALESCE(ts.lead_time_days, 2) AS LeadTimeDays, COALESCE(ts.safety_days, 1) AS SafetyDays,
               COALESCE(u.out28, 0) AS Out28, COALESCE(g.has_gauge, false) AS HasGauge,
               m.fact_volume AS LastFactVolume, m.ended_at AS LastFactAt, m.session_num AS LastFactSession, m.diff AS LastFactDiff
        FROM v_tank_book_stock b
        JOIN v_station_names sn ON sn.station_id = b.station_id
        LEFT JOIN records rt ON rt.station_id = b.station_id AND rt.dataset = 'dcTanks' AND rt.key = b.tank_id::text
        LEFT JOIN tank_settings ts ON ts.station_id = b.station_id AND ts.tank_id = b.tank_id
        LEFT JOIN use28 u ON u.station_id = b.station_id AND u.tank_id = b.tank_id
        LEFT JOIN v_tank_gauges g ON g.station_id = b.station_id AND g.tank_id = b.tank_id
        LEFT JOIN LATERAL (SELECT x.fact_volume, x.ended_at, x.session_num, x.diff FROM v_tank_measurements x
                           WHERE x.station_id = b.station_id AND x.tank_id = b.tank_id ORDER BY x.ended_at DESC LIMIT 1) m ON true
        WHERE b.station_id = ANY(@stations) AND COALESCE((rt.data->>'Deleted')::int, 0) = 0
        ORDER BY sn.station, b.tank_num
        """, new { stations })).ToList();
}
