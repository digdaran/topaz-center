using System.Globalization;
using System.Security.Claims;
using System.Text;
using Dapper;

namespace Topaz.Central.Ui;

public sealed record PurchaseSettingsBody(int[] TripWeekdays, int LeadDays, decimal ServiceA, decimal ServiceB, decimal ServiceC, int HistoryDays);
public sealed record ItemPlanBody(Guid StationId, string ItemKey, string Mode, decimal? MinQty, decimal? MaxQty, decimal? PackQty, string? Note);
public sealed record PurchaseLineBody(Guid StationId, long ItemId, string ItemKey, string? Name, string? GroupName, decimal SuggestedQty, decimal Qty, decimal? Price);
public sealed record PurchaseListBody(DateTime TripDate, string? Note, PurchaseLineBody[] Lines);

/// <summary>
/// Склад → Закупка (1.23): the shopping list for the next trip (cash and carry, all stations at once), the owner's
/// plan per item (core / do not buy / min / max / pack), saved lists and what Topaz has received of them.
/// Owner and manager only: the list carries purchase prices (a viewer gets 403).
/// </summary>
public static class PurchaseEndpoints
{
    private static readonly NumberFormatInfo Ru = RuNumbers.Format;

    public static void MapUiPurchasing(this RouteGroupBuilder ui)
    {
        var g = ui.MapGroup("/purchasing").RequireAuthorization(Policies.Manager);
        g.MapGet("/plan", Plan);
        g.MapGet("/settings", GetSettings);
        g.MapPut("/settings", SetSettings).RequireAuthorization(Policies.Owner);
        g.MapPut("/items", SetItem);
        g.MapGet("/lists", Lists);
        g.MapGet("/lists/{id:long}", ListCard);
        g.MapPost("/lists", CreateList);
        g.MapPut("/lists/{id:long}", UpdateList);
        g.MapPost("/lists/{id:long}/bought", (long id, ClaimsPrincipal user, UiDb db) => SetStatus(id, "bought", user, db));
        g.MapPost("/lists/{id:long}/close", (long id, ClaimsPrincipal user, UiDb db) => SetStatus(id, "closed", user, db));
        g.MapDelete("/lists/{id:long}", DeleteList);
        // Склад → Товары (1.24): retail figures only, so every role sees them.
        ui.MapGet("/analytics/availability", Availability);
    }

    // ---------------------------------------------------------------- plan

    private static async Task<PurchasePlanner.Settings> SettingsAsync(Npgsql.NpgsqlConnection c)
    {
        // A tuple, not the record: Dapper cannot pass an int[] column to a constructor.
        var rows = await c.QueryAsync<(int[] Days, int Lead, decimal A, decimal B, decimal C, int History)>(
            "SELECT trip_weekdays, lead_days, service_a, service_b, service_c, history_days FROM purchase_settings WHERE organization_id = app_org()");
        return rows.Select(r => new PurchasePlanner.Settings(r.Days, r.Lead, r.A, r.B, r.C, r.History)).SingleOrDefault() ?? PurchasePlanner.Settings.Default;
    }

    private static async Task<IResult> Availability(HttpContext ctx, StationScope scope, UiDb db)
    {
        var (fp, error) = await UiFilter.ParseAsync(ctx, scope);
        if (error is not null) return error;
        var f = fp!;
        await using var c = await db.OpenConnectionAsync(ctx.RequestAborted);
        var s = await SettingsAsync(c);
        var (stations, items) = ShopAvailability.Compute(await ShopAvailability.LoadAsync(c, f.Stations, f.From, f.To, s.HistoryDays));
        var withCore = stations.Where(x => x.Availability is not null).ToList();
        return Results.Ok(new
        {
            filter = OverviewEndpoints.Describe(f),
            total = new
            {
                // weighted by the core items of each station
                availability = withCore.Sum(x => x.CoreItems) is var n and > 0 ? Math.Round(withCore.Sum(x => x.Availability!.Value * x.CoreItems) / n, 4) : (decimal?)null,
                core_items = stations.Sum(x => x.CoreItems), core_marked = stations.Any(x => x.CoreMarked), core_out_now = stations.Sum(x => x.CoreOutNow),
                lost_revenue = stations.Sum(x => x.LostRevenue), out_items = stations.Sum(x => x.OutItems),
            },
            stations = stations.Select(x => new
            {
                station_id = x.StationId, station = x.Station, core_items = x.CoreItems, core_marked = x.CoreMarked, availability = x.Availability,
                core_out_now = x.CoreOutNow, out_items = x.OutItems, lost_revenue = x.LostRevenue,
            }),
            items = items.Take(200).Select(l => new
            {
                station_id = l.StationId, station = l.Station, item_key = l.ItemKey, name = l.Name, group_name = l.GroupName, core = l.Core,
                stock = l.Stock, period_days = l.PeriodDays, out_days = l.OutDays, per_day = l.PerDay, price = l.Price,
                lost_qty = l.LostQty, lost_revenue = l.LostRevenue,
            }),
        });
    }

    private static async Task<IResult> Plan(HttpContext ctx, StationScope scope, UiDb db)
    {
        var (fp, error) = await UiFilter.ParseAsync(ctx, scope);
        if (error is not null) return error;
        var f = fp!;
        await using var c = await db.OpenConnectionAsync(ctx.RequestAborted);
        var s = await SettingsAsync(c);
        var today = await c.ExecuteScalarAsync<DateTime?>(
            "SELECT (now() AT TIME ZONE time_zone)::date FROM v_station_names WHERE station_id = ANY(@st) ORDER BY station LIMIT 1", new { st = f.Stations })
            ?? DateTime.UtcNow.AddHours(3).Date;
        var (next, following) = PurchasePlanner.Trips(today, s.TripWeekdays);
        var horizon = (following - today).Days + s.LeadDays;
        var items = await PurchasePlanner.LoadAsync(c, f.Stations, s.HistoryDays);
        var lines = PurchasePlanner.Plan(items, s, horizon)
            .OrderBy(l => l.Status switch { "out" => 0, "low" => 1, "refill" => 2, "ok" => 3, _ => 4 })
            .ThenBy(l => l.GroupName ?? "яяя").ThenBy(l => l.Name).ToList();

        if (ctx.Request.Query["format"] == "csv")
            return Csv(lines.Where(l => l.Qty > 0).Select(l => (l.Station, l.StationId, l.ItemKey, l.Name, l.GroupName, l.Qty, l.Price)), $"zakupka-{next:yyyy-MM-dd}.csv");

        var buy = lines.Where(l => l.Qty > 0).ToList();
        return Results.Ok(new
        {
            settings = SettingsJson(s),
            today = today.ToString("yyyy-MM-dd"), next_trip = next.ToString("yyyy-MM-dd"), following_trip = following.ToString("yyyy-MM-dd"), horizon_days = horizon,
            lines = lines.Select(LineJson),
            totals = new
            {
                items = buy.Count, amount = buy.Sum(l => l.Amount ?? 0), unpriced = buy.Count(l => l.Price is null),
                out_of_stock = lines.Count(l => l.Status == "out"), core = lines.Count(l => l.Mode == "core"),
                // items that bring most of the revenue but are not marked "always in stock" yet
                core_candidates = lines.Count(l => l.Abc == "A" && l.Mode == "auto"),
            },
            stations = buy.GroupBy(l => (l.StationId, l.Station)).Select(g => new { station_id = g.Key.StationId, station = g.Key.Station, items = g.Count(), amount = g.Sum(l => l.Amount ?? 0) }),
        });
    }

    private static object SettingsJson(PurchasePlanner.Settings s) => new
    {
        trip_weekdays = s.TripWeekdays, lead_days = s.LeadDays, service_a = s.ServiceA, service_b = s.ServiceB, service_c = s.ServiceC, history_days = s.HistoryDays,
    };

    private static object LineJson(PurchasePlanner.Line l) => new
    {
        station_id = l.StationId, station = l.Station, item_id = l.ItemId, item_key = l.ItemKey, name = l.Name, group_name = l.GroupName,
        mode = l.Mode, abc = l.Abc, stock = l.Stock, on_order = l.OnOrder, per_day = l.PerDay, days_of_cover = l.DaysOfCover,
        in_stock_days = l.InStockDays, zero_days = l.ZeroDays, low_data = l.LowData, safety = l.Safety, target = l.Target,
        pack = l.Pack, pack_learned = l.PackLearned, qty = l.Qty, price = l.Price, amount = l.Amount, status = l.Status,
        min_qty = l.MinQty, max_qty = l.MaxQty, note = l.Note, last_sale = l.LastSale?.ToString("yyyy-MM-dd"),
    };

    /// <summary>One row per item, one column per station, then the total, the price and the sum: one list for one trip.</summary>
    private static IResult Csv(IEnumerable<(string Station, Guid StationId, string ItemKey, string? Name, string? Group, decimal Qty, decimal? Price)> rows, string file)
    {
        var list = rows.ToList();
        var stations = list.Select(r => (r.StationId, r.Station)).Distinct().OrderBy(s => s.Station).ToList();
        static string Q(string? v) => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
        string N(decimal v) => v.ToString("0.###", Ru);
        var sb = new StringBuilder("Группа;Товар;" + string.Join(';', stations.Select(s => Q(s.Station))) + ";Всего;Цена;Сумма\r\n");
        foreach (var g in list.GroupBy(r => r.ItemKey).OrderBy(g => g.First().Group ?? "яяя").ThenBy(g => g.First().Name))
        {
            var total = g.Sum(r => r.Qty);
            var price = g.Select(r => r.Price).FirstOrDefault(p => p is not null);
            sb.Append(Q(g.First().Group)).Append(';').Append(Q(g.First().Name)).Append(';');
            sb.Append(string.Join(';', stations.Select(s => g.Where(r => r.StationId == s.StationId).Sum(r => r.Qty) is var q and > 0 ? N(q) : "")));
            sb.Append(';').Append(N(total)).Append(';').Append(price is { } p ? p.ToString("0.00", Ru) : "")
              .Append(';').Append(price is { } p2 ? (p2 * total).ToString("0.00", Ru) : "").Append("\r\n");
        }
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return Results.File(bytes, "text/csv; charset=utf-8", file);
    }

    // ---------------------------------------------------------------- settings

    private static async Task<IResult> GetSettings(UiDb db)
    {
        await using var c = await db.OpenConnectionAsync();
        return Results.Ok(SettingsJson(await SettingsAsync(c)));
    }

    private static async Task<IResult> SetSettings(PurchaseSettingsBody body, ClaimsPrincipal user, UiDb db)
    {
        if (user.OrganizationId() is not { } org) return Results.Problem(statusCode: 400, title: "Выберите организацию");
        if (body.TripWeekdays is not { Length: >= 1 } || body.TripWeekdays.Any(d => d is < 1 or > 7))
            return Results.Problem(statusCode: 400, title: "Выберите хотя бы один день поездки");
        if (body.LeadDays is < 0 or > 14) return Results.Problem(statusCode: 400, title: "Срок до полки: от 0 до 14 дней");
        if (new[] { body.ServiceA, body.ServiceB, body.ServiceC }.Any(v => v is < 50 or > 99.9m)) return Results.Problem(statusCode: 400, title: "Уровень наличия: от 50 до 99,9 %");
        if (body.HistoryDays is < 14 or > 180) return Results.Problem(statusCode: 400, title: "История продаж: от 14 до 180 дней");
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync("""
            INSERT INTO purchase_settings (organization_id, trip_weekdays, lead_days, service_a, service_b, service_c, history_days, updated_by)
            VALUES (@org, @days, @LeadDays, @ServiceA, @ServiceB, @ServiceC, @HistoryDays, @by)
            ON CONFLICT (organization_id) DO UPDATE SET trip_weekdays = EXCLUDED.trip_weekdays, lead_days = EXCLUDED.lead_days,
                service_a = EXCLUDED.service_a, service_b = EXCLUDED.service_b, service_c = EXCLUDED.service_c,
                history_days = EXCLUDED.history_days, updated_by = EXCLUDED.updated_by, updated_at = now()
            """, new { org, days = body.TripWeekdays.Distinct().Order().ToArray(), body.LeadDays, body.ServiceA, body.ServiceB, body.ServiceC, body.HistoryDays, by = user.Identity?.Name });
        await Audit.WriteAsync(db, user, "purchase_settings_updated", "organization:" + org, body);
        return Results.NoContent();
    }

    /// <summary>The owner's plan for an item: mode auto with nothing else set removes the row.</summary>
    private static async Task<IResult> SetItem(ItemPlanBody body, ClaimsPrincipal user, StationScope scope, UiDb db)
    {
        if (!await scope.CanSeeAsync(user, body.StationId)) return Results.NotFound();
        if (body.Mode is not ("auto" or "core" or "stop")) return Results.Problem(statusCode: 400, title: "mode: auto | core | stop");
        if (string.IsNullOrWhiteSpace(body.ItemKey) || body.ItemKey.Length > 300) return Results.Problem(statusCode: 400, title: "item_key");
        if (body.MinQty is < 0 || body.MaxQty is <= 0 || body.PackQty is <= 0 || body.MinQty > body.MaxQty)
            return Results.Problem(statusCode: 400, title: "Минимум, максимум и упаковка — положительные, минимум не больше максимума");
        var note = string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim()[..Math.Min(body.Note.Trim().Length, 500)];
        await using var c = await db.OpenConnectionAsync();
        if (body.Mode == "auto" && body.MinQty is null && body.MaxQty is null && body.PackQty is null && note is null)
            await c.ExecuteAsync("DELETE FROM item_plan WHERE station_id = @StationId AND item_key = @ItemKey", body);
        else
            await c.ExecuteAsync("""
                INSERT INTO item_plan (station_id, item_key, mode, min_qty, max_qty, pack_qty, note, updated_by)
                VALUES (@StationId, @ItemKey, @Mode, @MinQty, @MaxQty, @PackQty, @note, @by)
                ON CONFLICT (station_id, item_key) DO UPDATE SET mode = EXCLUDED.mode, min_qty = EXCLUDED.min_qty, max_qty = EXCLUDED.max_qty,
                    pack_qty = EXCLUDED.pack_qty, note = EXCLUDED.note, updated_by = EXCLUDED.updated_by, updated_at = now()
                """, new { body.StationId, body.ItemKey, body.Mode, body.MinQty, body.MaxQty, body.PackQty, note, by = user.Identity?.Name });
        await Audit.WriteAsync(db, user, "item_plan_updated", $"station:{body.StationId}/item:{body.ItemKey}", body);
        return Results.NoContent();
    }

    // ---------------------------------------------------------------- lists

    private static async Task<IResult> Lists(UiDb db)
    {
        await using var c = await db.OpenConnectionAsync();
        // RLS: the organization's lists; lines of the stations this user can see.
        return Results.Ok(await c.QueryAsync("""
            SELECT pl.id, pl.trip_date, pl.status, pl.note, pl.created_by, pl.created_at, pl.bought_at, pl.closed_at,
                   count(l.item_id) FILTER (WHERE l.qty > 0) AS items, COALESCE(sum(l.qty * l.price), 0) AS amount,
                   count(l.item_id) FILTER (WHERE l.qty > 0 AND l.price IS NULL) AS unpriced,
                   COALESCE(sum(o.received_qty), 0) AS received_qty, COALESCE(sum(l.qty) FILTER (WHERE l.qty > 0), 0) AS qty,
                   count(o.item_id) FILTER (WHERE o.open_qty > 0) AS open_items
            FROM purchase_lists pl
            LEFT JOIN purchase_list_lines l ON l.list_id = pl.id
            LEFT JOIN v_purchase_on_order o ON o.list_id = l.list_id AND o.station_id = l.station_id AND o.item_id = l.item_id
            GROUP BY pl.id ORDER BY pl.created_at DESC LIMIT 100
            """));
    }

    private static async Task<IResult> ListCard(long id, HttpContext ctx, UiDb db)
    {
        await using var c = await db.OpenConnectionAsync();
        var head = await c.QuerySingleOrDefaultAsync("SELECT id, trip_date, status, note, created_by, created_at, bought_at, closed_at FROM purchase_lists WHERE id = @id", new { id });
        if (head is null) return Results.NotFound();
        var lines = (await c.QueryAsync<ListLine>("""
            SELECT l.station_id AS StationId, sn.station AS Station, l.item_id AS ItemId, l.item_key AS ItemKey, l.name AS Name, l.group_name AS GroupName,
                   l.suggested_qty AS SuggestedQty, l.qty AS Qty, l.price AS Price, o.received_qty AS ReceivedQty
            FROM purchase_list_lines l JOIN v_station_names sn ON sn.station_id = l.station_id
            LEFT JOIN v_purchase_on_order o ON o.list_id = l.list_id AND o.station_id = l.station_id AND o.item_id = l.item_id
            WHERE l.list_id = @id ORDER BY l.group_name, l.name, sn.station
            """, new { id })).ToList();
        if (ctx.Request.Query["format"] == "csv")
            return Csv(lines.Where(l => l.Qty > 0).Select(l => (l.Station, l.StationId, l.ItemKey, l.Name, l.GroupName, l.Qty, l.Price)),
                $"zakupka-{((DateTime)head.trip_date):yyyy-MM-dd}.csv");
        return Results.Ok(new
        {
            list = head,
            lines = lines.Select(l => new
            {
                station_id = l.StationId, station = l.Station, item_id = l.ItemId, item_key = l.ItemKey, name = l.Name, group_name = l.GroupName,
                suggested_qty = l.SuggestedQty, qty = l.Qty, price = l.Price, amount = l.Price is { } p ? Math.Round(p * l.Qty, 2) : (decimal?)null,
                received_qty = l.ReceivedQty,
            }),
        });
    }

    private sealed class ListLine
    {
        public Guid StationId { get; set; }
        public string Station { get; set; } = "";
        public long ItemId { get; set; }
        public string ItemKey { get; set; } = "";
        public string? Name { get; set; }
        public string? GroupName { get; set; }
        public decimal SuggestedQty { get; set; }
        public decimal Qty { get; set; }
        public decimal? Price { get; set; }
        public decimal? ReceivedQty { get; set; }
    }

    private static async Task<string?> ValidateAsync(PurchaseListBody body, ClaimsPrincipal user, StationScope scope)
    {
        if (body.Lines is null || body.Lines.Length is 0 or > 5000) return "В списке нет товаров";
        var allowed = await scope.AllowedAsync(user);
        if (body.Lines.Any(l => !allowed.Contains(l.StationId))) return "АЗС недоступна";
        if (body.Lines.Any(l => l.Qty is < 0 or > 1_000_000 || l.Price is < 0 || string.IsNullOrWhiteSpace(l.ItemKey)))
            return "Количество и цена — от 0";
        if (body.Lines.GroupBy(l => (l.StationId, l.ItemId)).Any(g => g.Count() > 1)) return "Товар повторяется";
        return null;
    }

    private static async Task InsertLinesAsync(Npgsql.NpgsqlConnection c, Npgsql.NpgsqlTransaction tx, long id, PurchaseLineBody[] lines)
    {
        foreach (var l in lines)
            await c.ExecuteAsync("""
                INSERT INTO purchase_list_lines (list_id, station_id, item_id, item_key, name, group_name, suggested_qty, qty, price)
                VALUES (@id, @StationId, @ItemId, @ItemKey, @Name, @GroupName, @SuggestedQty, @Qty, @Price)
                """, new { id, l.StationId, l.ItemId, l.ItemKey, Name = Cut(l.Name), GroupName = Cut(l.GroupName), l.SuggestedQty, l.Qty, l.Price }, tx);
    }

    private static string? Cut(string? s) => s is null ? null : s[..Math.Min(s.Length, 300)];

    private static async Task<IResult> CreateList(PurchaseListBody body, ClaimsPrincipal user, StationScope scope, UiDb db)
    {
        if (user.OrganizationId() is not { } org) return Results.Problem(statusCode: 400, title: "Выберите организацию");
        if (await ValidateAsync(body, user, scope) is { } problem) return Results.Problem(statusCode: 400, title: problem);
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var id = await c.ExecuteScalarAsync<long>(
            "INSERT INTO purchase_lists (organization_id, trip_date, note, created_by) VALUES (@org, @trip, @note, @by) RETURNING id",
            new { org, trip = body.TripDate.Date, note = Cut(body.Note), by = user.Identity?.Name }, tx);
        await InsertLinesAsync(c, tx, id, body.Lines);
        await tx.CommitAsync();
        await Audit.WriteAsync(db, user, "purchase_list_created", "purchase_list:" + id, new { trip_date = body.TripDate.Date, lines = body.Lines.Length });
        return Results.Ok(new { id });
    }

    /// <summary>A draft is saved again as a whole (the lines this user can see are replaced).</summary>
    private static async Task<IResult> UpdateList(long id, PurchaseListBody body, ClaimsPrincipal user, StationScope scope, UiDb db)
    {
        if (await ValidateAsync(body, user, scope) is { } problem) return Results.Problem(statusCode: 400, title: problem);
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var status = await c.ExecuteScalarAsync<string?>("SELECT status FROM purchase_lists WHERE id = @id FOR UPDATE", new { id }, tx);
        if (status is null) return Results.NotFound();
        if (status != "draft") return Results.Problem(statusCode: 409, title: "Список уже куплен — его не меняют");
        await c.ExecuteAsync("UPDATE purchase_lists SET trip_date = @trip, note = @note WHERE id = @id", new { id, trip = body.TripDate.Date, note = Cut(body.Note) }, tx);
        await c.ExecuteAsync("DELETE FROM purchase_list_lines WHERE list_id = @id", new { id }, tx);
        await InsertLinesAsync(c, tx, id, body.Lines);
        await tx.CommitAsync();
        await Audit.WriteAsync(db, user, "purchase_list_updated", "purchase_list:" + id, new { trip_date = body.TripDate.Date, lines = body.Lines.Length });
        return Results.NoContent();
    }

    /// <summary>draft → bought (its lines are on order until Topaz receives them), bought → closed (stop waiting).</summary>
    private static async Task<IResult> SetStatus(long id, string status, ClaimsPrincipal user, UiDb db)
    {
        await using var c = await db.OpenConnectionAsync();
        var changed = await c.ExecuteAsync(status == "bought"
            ? "UPDATE purchase_lists SET status = 'bought', bought_at = now() WHERE id = @id AND status = 'draft'"
            : "UPDATE purchase_lists SET status = 'closed', closed_at = now() WHERE id = @id AND status = 'bought'", new { id });
        if (changed == 0)
            return await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM purchase_lists WHERE id = @id)", new { id })
                ? Results.Problem(statusCode: 409, title: status == "bought" ? "Список уже отмечен купленным" : "Закрыть можно только купленный список")
                : Results.NotFound();
        await Audit.WriteAsync(db, user, status == "bought" ? "purchase_list_bought" : "purchase_list_closed", "purchase_list:" + id, null);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteList(long id, ClaimsPrincipal user, UiDb db)
    {
        await using var c = await db.OpenConnectionAsync();
        var deleted = await c.ExecuteAsync("DELETE FROM purchase_lists WHERE id = @id AND status = 'draft'", new { id });
        if (deleted == 0)
            return await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM purchase_lists WHERE id = @id)", new { id })
                ? Results.Problem(statusCode: 409, title: "Удалить можно только черновик")
                : Results.NotFound();
        await Audit.WriteAsync(db, user, "purchase_list_deleted", "purchase_list:" + id, null);
        return Results.NoContent();
    }
}
