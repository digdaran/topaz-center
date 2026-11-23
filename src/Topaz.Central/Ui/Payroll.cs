using Dapper;
using Npgsql;

namespace Topaz.Central.Ui;

/// <summary>
/// Operators' pay (1.33, Деньги → Зарплаты). An employee is a Topaz login of a station. Per shift and login:
/// the fixed part (each workplace pays one fixed, split by hours among its operators; nothing for a closed shift
/// shorter than the scheme's minimum), % of the fuel and shop revenue rung up at the operator's workplace (as in the
/// «Операторы» rating) and ₽ per litre. The scheme is the login's own one in force on the shift's opening date, else
/// the station's. Pure calculation here (<see cref="Compute"/>, <see cref="Summarize"/>), loading in <see cref="LoadAsync"/>.
/// </summary>
public static class Payroll
{
    public sealed class Scheme
    {
        public long Id { get; set; }
        public Guid StationId { get; set; }
        public string? Login { get; set; }
        public DateTime ValidFrom { get; set; }
        public decimal ShiftFixed { get; set; }
        public decimal FuelPct { get; set; }
        public decimal ShopPct { get; set; }
        public decimal PerLiter { get; set; }
        public decimal MinShiftHours { get; set; }
        public long[] ExcludedGroups { get; set; } = [];
        public string? UpdatedBy { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public sealed class Shift
    {
        public Guid StationId { get; set; }
        public string Station { get; set; } = "";
        public long SessionId { get; set; }
        public int? SessionNum { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public DateTime LocalDate { get; set; }
        public bool Excluded { get; set; }
        public int Alerts { get; set; }
    }

    /// <summary>An operator's time at a workplace in a shift (no workplace: a shift without Topaz local shifts).</summary>
    public sealed class Presence
    {
        public Guid StationId { get; set; }
        public long SessionId { get; set; }
        public long? WorkbenchId { get; set; }
        public string Login { get; set; } = "";
        public double Secs { get; set; }
    }

    /// <summary>Sales of a login in a shift: fuel (GroupId null) or the shop by item group.</summary>
    public sealed class Sales
    {
        public Guid StationId { get; set; }
        public long SessionId { get; set; }
        public string Login { get; set; } = "";
        public long? GroupId { get; set; }
        public decimal Fuel { get; set; }
        public decimal Liters { get; set; }
        public decimal Shop { get; set; }
    }

    public sealed class Adjustment
    {
        public long Id { get; set; }
        public Guid StationId { get; set; }
        public string Login { get; set; } = "";
        public DateTime Month { get; set; }
        public string Kind { get; set; } = "";
        public decimal Amount { get; set; }
        public DateTime OnDate { get; set; }
        public string? Comment { get; set; }
        public string? CreatedBy { get; set; }
    }

    public sealed record Input(List<Shift> Shifts, List<Presence> Presence, List<Sales> Sales, List<Scheme> Schemes, HashSet<(Guid, string)> NoPay);

    /// <summary>One login's pay for one shift.</summary>
    public sealed class ShiftPay
    {
        public Guid StationId { get; set; }
        public string Station { get; set; } = "";
        public long SessionId { get; set; }
        public int? SessionNum { get; set; }
        public DateTime LocalDate { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public bool Open { get; set; }
        public string Login { get; set; } = "";
        public decimal Hours { get; set; }
        public decimal FuelRevenue { get; set; }
        public decimal Liters { get; set; }
        public decimal ShopRevenue { get; set; }
        /// <summary>Shop revenue the % is paid from (without the scheme's excluded groups).</summary>
        public decimal ShopBase { get; set; }
        /// <summary>Share of the shift's fixed part (1 = whole).</summary>
        public decimal FixedShare { get; set; }
        public decimal Fixed { get; set; }
        public decimal FuelPay { get; set; }
        public decimal ShopPay { get; set; }
        public decimal LiterPay { get; set; }
        public decimal Total { get; set; }
        public bool Excluded { get; set; }
        /// <summary>A closed shift shorter than the scheme's minimum: no fixed part.</summary>
        public bool Short { get; set; }
        public long? SchemeId { get; set; }
        public int Alerts { get; set; }
    }

    /// <summary>One login's month (or period) at one station.</summary>
    public sealed class Employee
    {
        public Guid StationId { get; set; }
        public string Station { get; set; } = "";
        public string Login { get; set; } = "";
        public int Shifts { get; set; }
        public int OpenShifts { get; set; }
        public decimal Hours { get; set; }
        public decimal FuelRevenue { get; set; }
        public decimal Liters { get; set; }
        public decimal ShopRevenue { get; set; }
        public decimal Fixed { get; set; }
        public decimal FuelPay { get; set; }
        public decimal ShopPay { get; set; }
        public decimal LiterPay { get; set; }
        /// <summary>By the scheme: fixed + percentages + per litre.</summary>
        public decimal Accrued { get; set; }
        public decimal Bonus { get; set; }
        public decimal Fine { get; set; }
        /// <summary>Accrued + bonuses − fines.</summary>
        public decimal Total { get; set; }
        public decimal Advance { get; set; }
        /// <summary>Total − advances, rounded to the rouble.</summary>
        public decimal ToPay { get; set; }
        /// <summary>Shifts paid by no scheme (none set for their date).</summary>
        public int Unpaid { get; set; }
    }

    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>The scheme in force for a login at a station on a date: the login's own one, else the station's.</summary>
    public static Scheme? SchemeFor(IEnumerable<Scheme> schemes, Guid station, string login, DateTime date)
    {
        var own = schemes.Where(s => s.StationId == station && s.Login == login && s.ValidFrom.Date <= date.Date).MaxBy(s => s.ValidFrom);
        return own ?? schemes.Where(s => s.StationId == station && s.Login is null && s.ValidFrom.Date <= date.Date).MaxBy(s => s.ValidFrom);
    }

    public static List<ShiftPay> Compute(Input input, DateTime nowUtc)
    {
        var presence = input.Presence.ToLookup(p => (p.StationId, p.SessionId));
        var sales = input.Sales.ToLookup(p => (p.StationId, p.SessionId));
        var result = new List<ShiftPay>();
        foreach (var sh in input.Shifts)
        {
            var key = (sh.StationId, sh.SessionId);
            // Each workplace pays one fixed part, split by time among the operators who worked at it.
            var shares = new Dictionary<string, (decimal Share, double Secs)>();
            foreach (var place in presence[key].GroupBy(p => p.WorkbenchId))
            {
                var total = place.Sum(p => Math.Max(p.Secs, 0));
                var n = place.Count();
                foreach (var p in place)
                {
                    var share = total > 0 ? (decimal)(Math.Max(p.Secs, 0) / total) : 1m / n;
                    var was = shares.GetValueOrDefault(p.Login);
                    shares[p.Login] = (was.Share + share, was.Secs + Math.Max(p.Secs, 0));
                }
            }
            var bySales = sales[key].GroupBy(s => s.Login).ToDictionary(g => g.Key, g => g.ToList());
            var open = sh.EndedAt is null;
            var shiftHours = ((sh.EndedAt ?? nowUtc) - sh.StartedAt).TotalHours;
            foreach (var login in shares.Keys.Union(bySales.Keys).Order(StringComparer.Ordinal))
            {
                if (input.NoPay.Contains((sh.StationId, login))) continue;
                var rows = bySales.GetValueOrDefault(login) ?? [];
                var (share, secs) = shares.GetValueOrDefault(login);
                share = Math.Min(share, 1m);
                var pay = new ShiftPay
                {
                    StationId = sh.StationId, Station = sh.Station, SessionId = sh.SessionId, SessionNum = sh.SessionNum, LocalDate = sh.LocalDate,
                    StartedAt = sh.StartedAt, EndedAt = sh.EndedAt, Open = open, Login = login, Hours = Math.Round((decimal)secs / 3600m, 1),
                    FuelRevenue = rows.Sum(r => r.Fuel), Liters = rows.Sum(r => r.Liters), ShopRevenue = rows.Sum(r => r.Shop),
                    FixedShare = Math.Round(share, 4), Excluded = sh.Excluded, Alerts = sh.Alerts,
                };
                var scheme = SchemeFor(input.Schemes, sh.StationId, login, sh.LocalDate);
                if (scheme is not null)
                {
                    pay.SchemeId = scheme.Id;
                    pay.ShopBase = rows.Where(r => r.GroupId is not { } g || !scheme.ExcludedGroups.Contains(g)).Sum(r => r.Shop);
                    pay.Short = !open && shiftHours < (double)scheme.MinShiftHours;
                    if (!sh.Excluded)
                    {
                        pay.Fixed = pay.Short ? 0 : R2(scheme.ShiftFixed * share);
                        pay.FuelPay = R2(pay.FuelRevenue * scheme.FuelPct / 100m);
                        pay.ShopPay = R2(pay.ShopBase * scheme.ShopPct / 100m);
                        pay.LiterPay = R2(pay.Liters * scheme.PerLiter);
                        pay.Total = pay.Fixed + pay.FuelPay + pay.ShopPay + pay.LiterPay;
                    }
                }
                result.Add(pay);
            }
        }
        return result;
    }

    /// <summary>Per station and login: shifts, revenue, each part, manual lines and what is left to pay.</summary>
    public static List<Employee> Summarize(IEnumerable<ShiftPay> shifts, IEnumerable<Adjustment> adjustments)
    {
        var map = new Dictionary<(Guid, string), Employee>();
        Employee Get(Guid station, string stationName, string login)
        {
            if (!map.TryGetValue((station, login), out var e)) map[(station, login)] = e = new Employee { StationId = station, Station = stationName, Login = login };
            if (e.Station == "") e.Station = stationName;
            return e;
        }
        foreach (var s in shifts)
        {
            var e = Get(s.StationId, s.Station, s.Login);
            if (!s.Excluded) e.Shifts++;
            if (s.Open) e.OpenShifts++;
            if (s.SchemeId is null && !s.Excluded) e.Unpaid++;
            e.Hours += s.Hours; e.FuelRevenue += s.FuelRevenue; e.Liters += s.Liters; e.ShopRevenue += s.ShopRevenue;
            e.Fixed += s.Fixed; e.FuelPay += s.FuelPay; e.ShopPay += s.ShopPay; e.LiterPay += s.LiterPay; e.Accrued += s.Total;
        }
        foreach (var a in adjustments)
        {
            var e = Get(a.StationId, "", a.Login);
            switch (a.Kind)
            {
                case "bonus": e.Bonus += a.Amount; break;
                case "fine": e.Fine += a.Amount; break;
                case "advance": e.Advance += a.Amount; break;
            }
        }
        foreach (var e in map.Values)
        {
            e.Total = e.Accrued + e.Bonus - e.Fine;
            e.ToPay = Math.Round(e.Total - e.Advance, 0, MidpointRounding.AwayFromZero);
        }
        return map.Values.OrderBy(e => e.Station, StringComparer.CurrentCulture).ThenByDescending(e => e.Accrued).ThenBy(e => e.Login, StringComparer.Ordinal).ToList();
    }

    // ---------------------------------------------------------------- loading

    /// <summary>Shifts opened (station-local date) in [from, to] at these stations, without deleted ones.</summary>
    private const string ShiftsCte = """
        WITH s AS (
            SELECT t.station_id, sn.station, t.session_id, t.session_num, t.user_name, t.started_at, t.ended_at,
                   (t.started_at AT TIME ZONE sn.time_zone)::date AS local_date
            FROM shifts t JOIN v_station_names sn ON sn.station_id = t.station_id
            WHERE t.station_id = ANY(@st) AND NOT COALESCE(t.is_deleted, false) AND t.started_at IS NOT NULL
              AND t.started_at >= @utcFrom AND t.started_at < @utcTo
              AND (t.started_at AT TIME ZONE sn.time_zone)::date BETWEEN @from::date AND @to::date)
        """;

    public static async Task<Input> LoadAsync(NpgsqlConnection c, Guid[] stations, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var args = new
        {
            st = stations, from, to,
            utcFrom = DateTime.SpecifyKind(from.AddHours(-14), DateTimeKind.Utc), utcTo = DateTime.SpecifyKind(to.AddDays(1).AddHours(14), DateTimeKind.Utc),
        };
        var shifts = (await c.QueryAsync<Shift>(new CommandDefinition(ShiftsCte + """
            SELECT s.station_id, s.station, s.session_id, s.session_num, s.started_at, s.ended_at, s.local_date,
                   EXISTS (SELECT 1 FROM pay_shift_exclusions e WHERE e.station_id = s.station_id AND e.session_id = s.session_id) AS excluded,
                   (SELECT count(*) FROM alerts a WHERE a.station_id = s.station_id AND a.session_id = s.session_id)::int AS alerts
            FROM s ORDER BY s.station, s.started_at
            """, args, cancellationToken: ct))).ToList();
        var presence = (await c.QueryAsync<Presence>(new CommandDefinition(ShiftsCte + """
            SELECT v.station_id, v.session_id, v.workbench_id, v.operator AS login,
                   extract(epoch FROM COALESCE(v.ended_at, s.ended_at, now()) - COALESCE(v.started_at, s.started_at))::float8 AS secs
            FROM s JOIN v_workplace_shifts v ON v.station_id = s.station_id AND v.session_id = s.session_id
            UNION ALL
            SELECT s.station_id, s.session_id, NULL::bigint, s.user_name, extract(epoch FROM COALESCE(s.ended_at, now()) - s.started_at)::float8
            FROM s
            WHERE s.user_name IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM workplace_shifts x WHERE x.station_id = s.station_id AND x.session_id = s.session_id)
            """, args, cancellationToken: ct))).ToList();
        var sales = (await c.QueryAsync<Sales>(new CommandDefinition(ShiftsCte + """
            SELECT fs.station_id, fs.session_id, COALESCE(v.operator, s.user_name, '?') AS login, NULL::bigint AS group_id,
                   COALESCE(sum(fs.amount) FILTER (WHERE fs.payment_kind <> 'none'), 0) AS fuel,
                   COALESCE(sum(fs.volume) FILTER (WHERE fs.volume > 0 AND fs.payment_kind <> 'none'), 0) AS liters, 0::numeric AS shop
            FROM s JOIN fuel_sales fs ON fs.station_id = s.station_id AND fs.session_id = s.session_id
            LEFT JOIN v_workplace_shifts v ON v.station_id = fs.station_id AND v.local_session_id = fs.local_session_id AND v.session_id = fs.session_id
            GROUP BY 1, 2, 3
            UNION ALL
            SELECT r.station_id, r.session_id, COALESCE(v.operator, s.user_name, '?'), NULLIF(ri.data->>'ItemsGrpID', '')::bigint, 0, 0,
                   COALESCE(sum(CASE WHEN r.is_return THEN -l.total ELSE l.total END), 0)
            FROM s JOIN shop_receipts r ON r.station_id = s.station_id AND r.session_id = s.session_id
            JOIN shop_receipt_lines l ON l.station_id = r.station_id AND l.receipt_id = r.receipt_id
            LEFT JOIN records ri ON ri.station_id = l.station_id AND ri.dataset = 'dcItems' AND ri.key = l.item_id::text
            LEFT JOIN v_workplace_shifts v ON v.station_id = r.station_id AND v.local_session_id = r.local_session_id AND v.session_id = r.session_id
            GROUP BY 1, 2, 3, 4
            """, args, cancellationToken: ct))).ToList();
        var schemes = await SchemesAsync(c, stations, ct);
        var noPay = (await c.QueryAsync<(Guid, string)>(new CommandDefinition(
            "SELECT station_id, login FROM pay_employees WHERE no_pay AND station_id = ANY(@st)", args, cancellationToken: ct))).ToHashSet();
        return new Input(shifts, presence, sales, schemes, noPay);
    }

    public static async Task<List<Scheme>> SchemesAsync(NpgsqlConnection c, Guid[] stations, CancellationToken ct = default) =>
        (await c.QueryAsync<Scheme>(new CommandDefinition("""
            SELECT id, station_id, login, valid_from, shift_fixed, fuel_pct, shop_pct, per_liter, min_shift_hours, excluded_groups, updated_by, updated_at
            FROM pay_schemes WHERE station_id = ANY(@st) ORDER BY station_id, login NULLS FIRST, valid_from
            """, new { st = stations }, cancellationToken: ct))).ToList();

    public static async Task<List<Adjustment>> AdjustmentsAsync(NpgsqlConnection c, Guid[] stations, DateTime monthFrom, DateTime monthTo, CancellationToken ct = default) =>
        (await c.QueryAsync<Adjustment>(new CommandDefinition("""
            SELECT id, station_id, login, month, kind, amount, on_date, comment, created_by
            FROM pay_adjustments WHERE station_id = ANY(@st) AND month BETWEEN @monthFrom::date AND @monthTo::date ORDER BY on_date, id
            """, new { st = stations, monthFrom, monthTo }, cancellationToken: ct))).ToList();
}
