using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using Topaz.Contracts;

namespace Topaz.Central.Ui;

public sealed record PaySchemeBody(Guid StationId, string? Login, string ValidFrom, decimal ShiftFixed, decimal FuelPct, decimal ShopPct, decimal PerLiter,
    decimal MinShiftHours = 1, long[]? ExcludedGroups = null);
public sealed record PayEmployeeBody(Guid StationId, string Login, bool NoPay);
public sealed record PayAdjustmentBody(Guid StationId, string Login, string Month, string Kind, decimal Amount, string? OnDate, string? Comment);
public sealed record PayMonthBody(Guid StationId, string Month);
public sealed record PayExclusionBody(Guid StationId, long SessionId, bool Excluded);
public sealed record PayPayoutBody(Guid StationId, string Month, string Login, bool Paid, string? PaidOn);

/// <summary>
/// Деньги → Зарплаты (1.33): the month's payroll per station and login, payslips, closing a month (a frozen snapshot),
/// manual lines, shifts taken out, «Выплачено», and the schemes of pay. Owner only: managers and viewers get 403.
/// </summary>
public static class PayrollEndpoints
{
    private static readonly NumberFormatInfo Ru = RuNumbers.Format;
    private static readonly string[] Kinds = ["bonus", "fine", "advance"];

    public static void MapUiPayroll(this RouteGroupBuilder ui)
    {
        var g = ui.MapGroup("/payroll").RequireAuthorization(Policies.Owner);
        g.MapGet("", Sheet);
        g.MapGet("/slip", Slip);
        g.MapPost("/close", Close);
        g.MapPost("/reopen", Reopen);
        g.MapPost("/adjustments", AddAdjustment);
        g.MapDelete("/adjustments/{id:long}", DeleteAdjustment);
        g.MapPut("/exclusions", SetExclusion);
        g.MapPut("/payouts", SetPayout);
        g.MapGet("/schemes", Schemes);
        g.MapPost("/schemes", SaveScheme);
        g.MapDelete("/schemes/{id:long}", DeleteScheme);
        g.MapPut("/employees", SetEmployee);
    }

    private static DateTime TodayMoscow => DateTime.UtcNow.AddHours(3).Date;

    private static bool TryMonth(string? s, out DateTime month)
    {
        if (string.IsNullOrEmpty(s)) { month = new DateTime(TodayMoscow.Year, TodayMoscow.Month, 1); return true; }
        return DateTime.TryParseExact(s, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out month);
    }

    private static bool TryDate(string? s, out DateTime date) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static IResult Bad(string title) => Results.Problem(statusCode: 400, title: title);

    private sealed class Snapshot
    {
        public List<Payroll.Employee> Employees { get; set; } = [];
        public List<Payroll.ShiftPay> Shifts { get; set; } = [];
    }

    private sealed record MonthRow(Guid StationId, DateTime ClosedAt, string? ClosedBy, string Snapshot);
    private sealed record PayoutRow(Guid StationId, string Login, DateTime PaidOn, decimal Amount);

    private static async Task<bool> IsClosedAsync(NpgsqlConnection c, Guid station, DateTime month) =>
        await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM pay_months WHERE station_id = @station AND month = @month::date)", new { station, month });

    /// <summary>The live payroll of a month (the shifts opened in it) at these stations.</summary>
    private static async Task<(List<Payroll.ShiftPay> Shifts, List<Payroll.Employee> Employees, List<Payroll.Adjustment> Adjustments)> LiveAsync(
        NpgsqlConnection c, Guid[] stations, DateTime month, CancellationToken ct)
    {
        var input = await Payroll.LoadAsync(c, stations, month, month.AddMonths(1).AddDays(-1), ct);
        var shifts = Payroll.Compute(input, DateTime.UtcNow);
        var adjustments = await Payroll.AdjustmentsAsync(c, stations, month, month, ct);
        return (shifts, Payroll.Summarize(shifts, adjustments), adjustments);
    }

    /// <summary>
    /// Pay accrued for the shifts opened in a period plus bonuses − fines of the months starting in it (advances are payouts, not an
    /// expense). Null when no station of the period has a scheme: the organization does not keep salaries here.
    /// </summary>
    public static async Task<object?> ExpenseAsync(NpgsqlConnection c, Guid[] stations, DateTime from, DateTime to, CancellationToken ct)
    {
        if (!await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM pay_schemes WHERE station_id = ANY(@st))", new { st = stations })) return null;
        var shifts = Payroll.Compute(await Payroll.LoadAsync(c, stations, from, to, ct), DateTime.UtcNow);
        var lines = (await c.QueryAsync<(Guid StationId, decimal Bonus, decimal Fine)>("""
            SELECT station_id, COALESCE(sum(amount) FILTER (WHERE kind = 'bonus'), 0), COALESCE(sum(amount) FILTER (WHERE kind = 'fine'), 0)
            FROM pay_adjustments WHERE station_id = ANY(@st) AND month BETWEEN @from::date AND @to::date GROUP BY 1
            """, new { st = stations, from, to })).ToList();
        var accrued = shifts.Sum(s => s.Total);
        var bonus = lines.Sum(l => l.Bonus);
        var fine = lines.Sum(l => l.Fine);
        return new
        {
            accrued, bonus, fine, total = accrued + bonus - fine,
            by_station = stations.Select(st => new
            {
                station_id = st,
                total = shifts.Where(s => s.StationId == st).Sum(s => s.Total) + lines.Where(l => l.StationId == st).Sum(l => l.Bonus - l.Fine),
            }),
        };
    }

    // ---------------------------------------------------------------- payroll

    private static async Task<IResult> Sheet(HttpContext ctx, StationScope scope, UiDb db)
    {
        var (f, error) = await UiFilter.ParseAsync(ctx, scope);
        if (error is not null) return error;
        if (!TryMonth(ctx.Request.Query["month"], out var month)) return Bad("month: yyyy-MM");
        await using var c = await db.OpenConnectionAsync(ctx.RequestAborted);
        var (liveShifts, live, _) = await LiveAsync(c, f!.Stations, month, ctx.RequestAborted);
        var names = (await c.QueryAsync<(Guid Id, string Name)>("SELECT station_id, station FROM v_station_names WHERE station_id = ANY(@st)", new { st = f.Stations }))
            .ToDictionary(x => x.Id, x => x.Name);
        var months = (await c.QueryAsync<MonthRow>("SELECT station_id, closed_at, closed_by, snapshot::text AS snapshot FROM pay_months WHERE station_id = ANY(@st) AND month = @month::date",
            new { st = f.Stations, month })).ToDictionary(m => m.StationId);
        var payouts = (await c.QueryAsync<PayoutRow>("SELECT station_id, login, paid_on, amount FROM pay_payouts WHERE station_id = ANY(@st) AND month = @month::date",
            new { st = f.Stations, month })).ToDictionary(p => (p.StationId, p.Login));
        var withScheme = (await c.QueryAsync<Guid>("SELECT DISTINCT station_id FROM pay_schemes WHERE station_id = ANY(@st) AND valid_from <= @end::date",
            new { st = f.Stations, end = month.AddMonths(1).AddDays(-1) })).ToHashSet();

        var stations = new List<object>();
        var all = new List<Payroll.Employee>();
        foreach (var (id, name) in names.OrderBy(n => n.Value, StringComparer.CurrentCulture))
        {
            var liveRows = live.Where(e => e.StationId == id).ToList();
            var closed = months.GetValueOrDefault(id);
            var rows = liveRows;
            decimal? was = null;
            if (closed is not null)
            {
                rows = JsonSerializer.Deserialize<Snapshot>(closed.Snapshot, Protocol.Json)!.Employees;
                var then = rows.Sum(e => e.Total);
                var now = liveRows.Sum(e => e.Total);
                if (then != now) was = then;
            }
            all.AddRange(rows);
            stations.Add(new
            {
                station_id = id, station = name,
                closed_at = closed?.ClosedAt, closed_by = closed?.ClosedBy,
                // after closing, the data from Topaz changed what the month would accrue now
                changed = was is null ? null : new { was, now = liveRows.Sum(e => e.Total) },
                has_scheme = withScheme.Contains(id),
                open_shifts = liveShifts.Where(s => s.StationId == id && s.Open).Select(s => s.SessionId).Distinct().Count(),
                employees = rows.Select(e => new
                {
                    e.Login, e.Shifts, e.OpenShifts, e.Hours, e.FuelRevenue, e.Liters, e.ShopRevenue, e.Fixed, e.FuelPay, e.ShopPay, e.LiterPay,
                    e.Accrued, e.Bonus, e.Fine, e.Total, e.Advance, e.ToPay, e.Unpaid,
                    paid = payouts.GetValueOrDefault((id, e.Login)) is { } p ? new { paid_on = p.PaidOn, p.Amount } : null,
                }),
                totals = Totals(rows),
            });
        }
        if (ctx.Request.Query["format"] == "csv") return Csv(all, months.Keys.ToHashSet(), payouts, month);
        return Results.Ok(new { month = month.ToString("yyyy-MM"), stations, totals = Totals(all) });
    }

    private static object Totals(List<Payroll.Employee> rows) => new
    {
        accrued = rows.Sum(e => e.Accrued), bonus = rows.Sum(e => e.Bonus), fine = rows.Sum(e => e.Fine), total = rows.Sum(e => e.Total),
        advance = rows.Sum(e => e.Advance), to_pay = rows.Sum(e => e.ToPay), employees = rows.Count,
    };

    private static IResult Csv(List<Payroll.Employee> rows, HashSet<Guid> closed, Dictionary<(Guid, string), PayoutRow> payouts, DateTime month)
    {
        static string Q(string v) => "\"" + v.Replace("\"", "\"\"") + "\"";
        string M(decimal v) => v.ToString("0.00", Ru);
        var sb = new StringBuilder("АЗС;Сотрудник;Смен;Часов;Выручка топливо;Литры;Выручка магазин;Фикс;% топливо;% магазин;За литры;Начислено по схеме;Премии;Штрафы;Итого начислено;Авансы;К выдаче;Месяц закрыт;Выплачено\r\n");
        foreach (var e in rows)
            sb.Append(string.Join(';', Q(e.Station), Q(e.Login), e.Shifts, e.Hours.ToString("0.#", Ru), M(e.FuelRevenue), e.Liters.ToString("0.##", Ru), M(e.ShopRevenue),
                M(e.Fixed), M(e.FuelPay), M(e.ShopPay), M(e.LiterPay), M(e.Accrued), M(e.Bonus), M(e.Fine), M(e.Total), M(e.Advance), M(e.ToPay),
                closed.Contains(e.StationId) ? "да" : "нет",
                payouts.GetValueOrDefault((e.StationId, e.Login)) is { } p ? p.PaidOn.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : "")).Append("\r\n");
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return Results.File(bytes, "text/csv; charset=utf-8", $"zarplaty-{month:yyyy-MM}.csv");
    }

    /// <summary>A payslip: one login's shifts of the month with each part, manual lines and the total.</summary>
    private static async Task<IResult> Slip(HttpContext ctx, StationScope scope, UiDb db)
    {
        var q = ctx.Request.Query;
        if (!Guid.TryParse(q["station"], out var station) || string.IsNullOrEmpty(q["login"])) return Bad("station и login обязательны");
        if (!TryMonth(q["month"], out var month)) return Bad("month: yyyy-MM");
        if (!await scope.CanSeeAsync(ctx.User, station)) return Results.NotFound();
        var login = q["login"].ToString();
        await using var c = await db.OpenConnectionAsync(ctx.RequestAborted);
        var closed = await c.QuerySingleOrDefaultAsync<MonthRow>("SELECT station_id, closed_at, closed_by, snapshot::text AS snapshot FROM pay_months WHERE station_id = @station AND month = @month::date",
            new { station, month });
        List<Payroll.ShiftPay> shifts;
        List<Payroll.Employee> employees;
        if (closed is not null)
        {
            var snap = JsonSerializer.Deserialize<Snapshot>(closed.Snapshot, Protocol.Json)!;
            (shifts, employees) = (snap.Shifts, snap.Employees);
        }
        else (shifts, employees, _) = await LiveAsync(c, [station], month, ctx.RequestAborted);
        var employee = employees.SingleOrDefault(e => e.Login == login);
        if (employee is null) return Results.NotFound();
        var adjustments = (await Payroll.AdjustmentsAsync(c, [station], month, month, ctx.RequestAborted)).Where(a => a.Login == login);
        var schemes = await Payroll.SchemesAsync(c, [station], ctx.RequestAborted);
        var paid = await c.QuerySingleOrDefaultAsync<PayoutRow>("SELECT station_id, login, paid_on, amount FROM pay_payouts WHERE station_id = @station AND month = @month::date AND login = @login",
            new { station, month, login });
        return Results.Ok(new
        {
            station_id = station, station = await c.ExecuteScalarAsync<string>("SELECT station FROM v_station_names WHERE station_id = @station", new { station }),
            login, month = month.ToString("yyyy-MM"), closed_at = closed?.ClosedAt, employee,
            shifts = shifts.Where(s => s.Login == login).OrderBy(s => s.StartedAt),
            adjustments,
            schemes = schemes.Where(s => shifts.Any(x => x.Login == login && x.SchemeId == s.Id)),
            paid = paid is null ? null : new { paid_on = paid.PaidOn, paid.Amount },
        });
    }

    // ---------------------------------------------------------------- closing a month

    private static async Task<IResult> Close(PayMonthBody body, ClaimsPrincipal user, StationScope scope, UiDb db)
    {
        if (!await scope.CanSeeAsync(user, body.StationId)) return Results.NotFound();
        if (!TryMonth(body.Month, out var month) || string.IsNullOrEmpty(body.Month)) return Bad("month: yyyy-MM");
        if (month.AddMonths(1) > TodayMoscow) return Bad("Месяц ещё не закончился — закрыть его можно с 1-го числа следующего");
        await using var c = await db.OpenConnectionAsync();
        if (await IsClosedAsync(c, body.StationId, month)) return Results.Problem(statusCode: 409, title: "Месяц уже закрыт");
        var (shifts, employees, _) = await LiveAsync(c, [body.StationId], month, CancellationToken.None);
        if (shifts.Any(s => s.Open)) return Results.Problem(statusCode: 409, title: "В этом месяце есть незакрытая смена — закройте её в Топазе и дождитесь данных");
        var snapshot = JsonSerializer.Serialize(new Snapshot { Employees = employees, Shifts = shifts }, Protocol.Json);
        await c.ExecuteAsync("INSERT INTO pay_months (station_id, month, closed_by, snapshot) VALUES (@StationId, @month::date, @by, @snapshot::jsonb)",
            new { body.StationId, month, by = user.Identity?.Name, snapshot });
        await Audit.WriteAsync(db, user, "payroll_month_closed", $"station:{body.StationId}/month:{month:yyyy-MM}", new { total = employees.Sum(e => e.Total), employees = employees.Count });
        return Results.NoContent();
    }

    private static async Task<IResult> Reopen(PayMonthBody body, ClaimsPrincipal user, StationScope scope, UiDb db)
    {
        if (!await scope.CanSeeAsync(user, body.StationId)) return Results.NotFound();
        if (!TryMonth(body.Month, out var month) || string.IsNullOrEmpty(body.Month)) return Bad("month: yyyy-MM");
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var n = await c.ExecuteAsync("DELETE FROM pay_months WHERE station_id = @StationId AND month = @month::date", new { body.StationId, month }, tx);
        if (n == 0) return Results.NotFound();
        // «Выплачено» was about the frozen sums; they are gone with them.
        await c.ExecuteAsync("DELETE FROM pay_payouts WHERE station_id = @StationId AND month = @month::date", new { body.StationId, month }, tx);
        await tx.CommitAsync();
        await Audit.WriteAsync(db, user, "payroll_month_reopened", $"station:{body.StationId}/month:{month:yyyy-MM}", null);
        return Results.NoContent();
    }

    // ---------------------------------------------------------------- manual lines, shifts, payouts

    private static async Task<IResult> AddAdjustment(PayAdjustmentBody body, ClaimsPrincipal user, StationScope scope, UiDb db)
    {
        if (!await scope.CanSeeAsync(user, body.StationId)) return Results.NotFound();
        if (!TryMonth(body.Month, out var month) || string.IsNullOrEmpty(body.Month)) return Bad("month: yyyy-MM");
        if (!Kinds.Contains(body.Kind)) return Bad("kind: bonus | fine | advance");
        var login = body.Login?.Trim() ?? "";
        if (login.Length is 0 or > 100) return Bad("Укажите сотрудника");
        if (body.Amount is <= 0 or > 10_000_000) return Bad("Сумма должна быть больше нуля");
        var onDate = TodayMoscow;
        if (!string.IsNullOrEmpty(body.OnDate) && !TryDate(body.OnDate, out onDate)) return Bad("on_date: yyyy-MM-dd");
        var comment = string.IsNullOrWhiteSpace(body.Comment) ? null : body.Comment.Trim()[..Math.Min(body.Comment.Trim().Length, 300)];
        await using var c = await db.OpenConnectionAsync();
        if (await IsClosedAsync(c, body.StationId, month)) return Results.Problem(statusCode: 409, title: "Месяц закрыт — откройте его заново, чтобы что-то изменить");
        var id = await c.ExecuteScalarAsync<long>("""
            INSERT INTO pay_adjustments (station_id, login, month, kind, amount, on_date, comment, created_by)
            VALUES (@StationId, @login, @month::date, @Kind, @Amount, @onDate::date, @comment, @by) RETURNING id
            """, new { body.StationId, login, month, body.Kind, body.Amount, onDate, comment, by = user.Identity?.Name });
        await Audit.WriteAsync(db, user, "payroll_adjustment_added", $"station:{body.StationId}/month:{month:yyyy-MM}", new { id, login, body.Kind, body.Amount });
        return Results.Ok(new { id });
    }

    private static async Task<IResult> DeleteAdjustment(long id, ClaimsPrincipal user, UiDb db)
    {
        await using var c = await db.OpenConnectionAsync();
        var row = await c.QuerySingleOrDefaultAsync<(Guid StationId, DateTime Month)>("SELECT station_id, month FROM pay_adjustments WHERE id = @id", new { id });
        if (row == default) return Results.NotFound(); // RLS: other organizations' lines are not there
        if (await IsClosedAsync(c, row.StationId, row.Month)) return Results.Problem(statusCode: 409, title: "Месяц закрыт — откройте его заново, чтобы что-то изменить");
        await c.ExecuteAsync("DELETE FROM pay_adjustments WHERE id = @id", new { id });
        await Audit.WriteAsync(db, user, "payroll_adjustment_deleted", $"station:{row.StationId}/month:{row.Month:yyyy-MM}", new { id });
        return Results.NoContent();
    }

    private static async Task<IResult> SetExclusion(PayExclusionBody body, ClaimsPrincipal user, StationScope scope, UiDb db)
    {
        if (!await scope.CanSeeAsync(user, body.StationId)) return Results.NotFound();
        await using var c = await db.OpenConnectionAsync();
        var date = await c.ExecuteScalarAsync<DateTime?>("""
            SELECT (t.started_at AT TIME ZONE sn.time_zone)::date FROM shifts t JOIN v_station_names sn ON sn.station_id = t.station_id
            WHERE t.station_id = @StationId AND t.session_id = @SessionId
            """, body);
        if (date is null) return Results.NotFound();
        if (await IsClosedAsync(c, body.StationId, new DateTime(date.Value.Year, date.Value.Month, 1)))
            return Results.Problem(statusCode: 409, title: "Месяц закрыт — откройте его заново, чтобы что-то изменить");
        if (body.Excluded)
            await c.ExecuteAsync("INSERT INTO pay_shift_exclusions (station_id, session_id, excluded_by) VALUES (@StationId, @SessionId, @by) ON CONFLICT DO NOTHING",
                new { body.StationId, body.SessionId, by = user.Identity?.Name });
        else
            await c.ExecuteAsync("DELETE FROM pay_shift_exclusions WHERE station_id = @StationId AND session_id = @SessionId", body);
        await Audit.WriteAsync(db, user, "payroll_shift_excluded", $"station:{body.StationId}/session:{body.SessionId}", new { body.Excluded });
        return Results.NoContent();
    }

    private static async Task<IResult> SetPayout(PayPayoutBody body, ClaimsPrincipal user, StationScope scope, UiDb db)
    {
        if (!await scope.CanSeeAsync(user, body.StationId)) return Results.NotFound();
        if (!TryMonth(body.Month, out var month) || string.IsNullOrEmpty(body.Month)) return Bad("month: yyyy-MM");
        await using var c = await db.OpenConnectionAsync();
        var snap = await c.ExecuteScalarAsync<string?>("SELECT snapshot::text FROM pay_months WHERE station_id = @StationId AND month = @month::date", new { body.StationId, month });
        if (snap is null) return Results.Problem(statusCode: 409, title: "Сначала закройте месяц: отметка «Выплачено» ставится на закрытые суммы");
        if (!body.Paid)
        {
            await c.ExecuteAsync("DELETE FROM pay_payouts WHERE station_id = @StationId AND month = @month::date AND login = @Login", new { body.StationId, month, body.Login });
        }
        else
        {
            var employee = JsonSerializer.Deserialize<Snapshot>(snap, Protocol.Json)!.Employees.SingleOrDefault(e => e.Login == body.Login);
            if (employee is null) return Results.NotFound();
            var paidOn = TodayMoscow;
            if (!string.IsNullOrEmpty(body.PaidOn) && !TryDate(body.PaidOn, out paidOn)) return Bad("paid_on: yyyy-MM-dd");
            await c.ExecuteAsync("""
                INSERT INTO pay_payouts (station_id, month, login, paid_on, amount, marked_by) VALUES (@StationId, @month::date, @Login, @paidOn::date, @amount, @by)
                ON CONFLICT (station_id, month, login) DO UPDATE SET paid_on = EXCLUDED.paid_on, amount = EXCLUDED.amount, marked_by = EXCLUDED.marked_by, marked_at = now()
                """, new { body.StationId, month, body.Login, paidOn, amount = employee.ToPay, by = user.Identity?.Name });
        }
        await Audit.WriteAsync(db, user, "payroll_payout", $"station:{body.StationId}/month:{month:yyyy-MM}", new { body.Login, body.Paid });
        return Results.NoContent();
    }

    // ---------------------------------------------------------------- schemes and employees

    private static async Task<IResult> Schemes(HttpContext ctx, StationScope scope, UiDb db)
    {
        var (f, error) = await UiFilter.ParseAsync(ctx, scope);
        if (error is not null) return error;
        await using var c = await db.OpenConnectionAsync(ctx.RequestAborted);
        var schemes = await Payroll.SchemesAsync(c, f!.Stations, ctx.RequestAborted);
        // Employees: the station's Topaz logins, those who worked shifts in the last half year, and those with settings.
        var employees = (await c.QueryAsync<(Guid StationId, string Login, bool NoPay, DateTime? LastShift)>("""
            WITH logins AS (
                SELECT station_id, trim(login) AS login FROM station_users WHERE station_id = ANY(@st) AND NOT COALESCE(is_deleted, false) AND trim(COALESCE(login, '')) <> ''
                UNION SELECT station_id, login FROM pay_employees WHERE station_id = ANY(@st)
                UNION SELECT station_id, login FROM pay_schemes WHERE station_id = ANY(@st) AND login IS NOT NULL),
            worked AS (
                SELECT v.station_id, v.operator AS login, max(v.started_at) AS last_shift
                FROM v_workplace_shifts v WHERE v.station_id = ANY(@st) AND v.started_at > now() - interval '180 days' GROUP BY 1, 2
                UNION ALL
                SELECT t.station_id, t.user_name, max(t.started_at) FROM shifts t
                WHERE t.station_id = ANY(@st) AND t.started_at > now() - interval '180 days' AND t.user_name IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM workplace_shifts x WHERE x.station_id = t.station_id AND x.session_id = t.session_id)
                GROUP BY 1, 2)
            SELECT a.station_id, a.login, COALESCE(pe.no_pay, false) AS no_pay, (SELECT max(w.last_shift) FROM worked w WHERE w.station_id = a.station_id AND w.login = a.login) AS last_shift
            FROM (SELECT station_id, login FROM logins UNION SELECT station_id, login FROM worked) a
            LEFT JOIN pay_employees pe ON pe.station_id = a.station_id AND pe.login = a.login
            ORDER BY 4 DESC NULLS LAST, 2
            """, new { st = f.Stations })).ToList();
        var groups = (await c.QueryAsync<(Guid StationId, long Id, string Name)>("""
            SELECT station_id, key::bigint, COALESCE(NULLIF(trim(data->>'Name'), ''), '#' || key) FROM records
            WHERE dataset = 'dcItemsGrps' AND NOT deleted AND station_id = ANY(@st) AND key ~ '^-?[0-9]+$' ORDER BY 3
            """, new { st = f.Stations })).ToList();
        var names = await c.QueryAsync<(Guid Id, string Name)>("SELECT station_id, station FROM v_station_names WHERE station_id = ANY(@st) ORDER BY station", new { st = f.Stations });
        return Results.Ok(names.Select(n => new
        {
            station_id = n.Id, station = n.Name,
            schemes = schemes.Where(s => s.StationId == n.Id && s.Login is null).OrderByDescending(s => s.ValidFrom),
            employees = employees.Where(e => e.StationId == n.Id).Select(e => new
            {
                login = e.Login, no_pay = e.NoPay, last_shift = e.LastShift,
                schemes = schemes.Where(s => s.StationId == n.Id && s.Login == e.Login).OrderByDescending(s => s.ValidFrom),
            }),
            groups = groups.Where(g => g.StationId == n.Id).Select(g => new { id = g.Id, name = g.Name }),
        }));
    }

    private static async Task<IResult> SaveScheme(PaySchemeBody body, ClaimsPrincipal user, StationScope scope, UiDb db)
    {
        if (!await scope.CanSeeAsync(user, body.StationId)) return Results.NotFound();
        if (!TryDate(body.ValidFrom, out var from)) return Bad("Укажите дату «действует с»");
        var login = string.IsNullOrWhiteSpace(body.Login) ? null : body.Login.Trim();
        if (login is { Length: > 100 }) return Bad("login");
        if (body.ShiftFixed is < 0 or > 1_000_000) return Bad("Фикс за смену: от 0 до 1 000 000 ₽");
        if (body.FuelPct is < 0 or > 100 || body.ShopPct is < 0 or > 100) return Bad("Процент: от 0 до 100");
        if (body.PerLiter is < 0 or > 1000) return Bad("За литр: от 0 до 1000 ₽");
        if (body.MinShiftHours is < 0 or > 48) return Bad("Короткая смена: от 0 до 48 часов");
        if (body.ShiftFixed == 0 && body.FuelPct == 0 && body.ShopPct == 0 && body.PerLiter == 0) return Bad("Заполните хотя бы одну часть оплаты");
        var groups = (body.ExcludedGroups ?? []).Distinct().Order().ToArray();
        await using var c = await db.OpenConnectionAsync();
        var id = await c.ExecuteScalarAsync<long>("""
            INSERT INTO pay_schemes (station_id, login, valid_from, shift_fixed, fuel_pct, shop_pct, per_liter, min_shift_hours, excluded_groups, updated_by)
            VALUES (@StationId, @login, @from::date, @ShiftFixed, @FuelPct, @ShopPct, @PerLiter, @MinShiftHours, @groups, @by)
            ON CONFLICT (station_id, COALESCE(login, ''), valid_from) DO UPDATE SET shift_fixed = EXCLUDED.shift_fixed, fuel_pct = EXCLUDED.fuel_pct,
                shop_pct = EXCLUDED.shop_pct, per_liter = EXCLUDED.per_liter, min_shift_hours = EXCLUDED.min_shift_hours,
                excluded_groups = EXCLUDED.excluded_groups, updated_by = EXCLUDED.updated_by, updated_at = now()
            RETURNING id
            """, new { body.StationId, login, from, body.ShiftFixed, body.FuelPct, body.ShopPct, body.PerLiter, body.MinShiftHours, groups, by = user.Identity?.Name });
        await Audit.WriteAsync(db, user, "pay_scheme_saved", $"station:{body.StationId}", new { id, login, valid_from = from.ToString("yyyy-MM-dd"), body.ShiftFixed, body.FuelPct, body.ShopPct, body.PerLiter, body.MinShiftHours, groups });
        return Results.Ok(new { id });
    }

    private static async Task<IResult> DeleteScheme(long id, ClaimsPrincipal user, UiDb db)
    {
        await using var c = await db.OpenConnectionAsync();
        var station = await c.ExecuteScalarAsync<Guid?>("DELETE FROM pay_schemes WHERE id = @id RETURNING station_id", new { id });
        if (station is null) return Results.NotFound();
        await Audit.WriteAsync(db, user, "pay_scheme_deleted", $"station:{station}", new { id });
        return Results.NoContent();
    }

    private static async Task<IResult> SetEmployee(PayEmployeeBody body, ClaimsPrincipal user, StationScope scope, UiDb db)
    {
        if (!await scope.CanSeeAsync(user, body.StationId)) return Results.NotFound();
        var login = body.Login?.Trim() ?? "";
        if (login.Length is 0 or > 100) return Bad("login");
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync("""
            INSERT INTO pay_employees (station_id, login, no_pay, updated_by) VALUES (@StationId, @login, @NoPay, @by)
            ON CONFLICT (station_id, login) DO UPDATE SET no_pay = EXCLUDED.no_pay, updated_by = EXCLUDED.updated_by, updated_at = now()
            """, new { body.StationId, login, body.NoPay, by = user.Identity?.Name });
        await Audit.WriteAsync(db, user, "pay_employee_updated", $"station:{body.StationId}", new { login, body.NoPay });
        return Results.NoContent();
    }
}
