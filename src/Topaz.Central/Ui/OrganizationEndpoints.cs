using System.Security.Claims;
using Dapper;
using Npgsql;

namespace Topaz.Central.Ui;

public sealed record OrganizationBody(string? Name, bool? IsActive);
public sealed record MoveStationBody(Guid OrganizationId);
public sealed record OrganizationSettingsBody(string? MarginVat, decimal? VatRate);

/// <summary>Organizations (station owners sharing the server), superadmin only.</summary>
public static class OrganizationEndpoints
{
    public static void MapUiOrganizations(this RouteGroupBuilder ui)
    {
        var super = ui.MapGroup("").RequireAuthorization(Policies.Super);
        super.MapGet("/organizations", List);
        super.MapPost("/organizations", Create);
        super.MapPatch("/organizations/{id:guid}", Update);
        super.MapPost("/stations/{id:guid}/organization", MoveStation);

        // The current organization's own settings (owner, or the superadmin for the organization picked in the header).
        var owner = ui.MapGroup("").RequireAuthorization(Policies.Owner);
        owner.MapGet("/organization/settings", Settings);
        owner.MapPut("/organization/settings", SetSettings);
        owner.MapGet("/payment-modes", PaymentModes);
        owner.MapPut("/payment-modes/{station:guid}/{mode:long}", SetPaymentMode);
    }

    private static async Task<IResult> Settings(UiDb db)
    {
        await using var c = await db.OpenConnectionAsync();
        var row = await c.QuerySingleOrDefaultAsync("SELECT id, name, margin_vat, vat_rate FROM organizations WHERE id = app_org()");
        return row is null ? Results.Problem(statusCode: 400, title: "Выберите организацию") : Results.Ok(row);
    }

    /// <summary>Margin with VAT or without it (VAT at the rate taken out of receipted sales): every margin is recomputed.</summary>
    private static async Task<IResult> SetSettings(OrganizationSettingsBody body, ClaimsPrincipal user, UiDb db, Processing.RollupRefresher rollups)
    {
        if (body.MarginVat is not null and not ("gross" or "net")) return Results.Problem(statusCode: 400, title: "margin_vat: gross | net");
        if (body.VatRate is < 0 or >= 100) return Results.Problem(statusCode: 400, title: "Ставка НДС: от 0 до 99 %");
        if (user.OrganizationId() is not { } org) return Results.Problem(statusCode: 400, title: "Выберите организацию");
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await c.ExecuteAsync("UPDATE organizations SET margin_vat = COALESCE(@MarginVat, margin_vat), vat_rate = COALESCE(@VatRate, vat_rate) WHERE id = @org",
            new { body.MarginVat, body.VatRate, org }, tx);
        await c.ExecuteAsync("INSERT INTO rollup_dirty (station_id, kind) SELECT id, 'all' FROM stations WHERE organization_id = @org", new { org }, tx);
        await tx.CommitAsync();
        rollups.Notify();
        await Audit.WriteAsync(db, user, "organization_settings_updated", "organization:" + org, body);
        return Results.NoContent();
    }

    /// <summary>Payment modes of the visible stations; a discount mode lowers the pump price legitimately (no alert).</summary>
    private static async Task<IResult> PaymentModes(ClaimsPrincipal user, StationScope scope, UiDb db)
    {
        var st = await scope.AllowedAsync(user);
        await using var c = await db.OpenConnectionAsync();
        // Hint for the owner: paid sales of the last 90 days and how many of them Topaz booked into the cash drawer
        // (rgCashes, fuel dispensing): cash goes there, cards and contracts never do.
        return Results.Ok(await c.QueryAsync("""
            WITH s AS (SELECT f.station_id, f.payment_mode_id, f.order_id FROM fuel_sales f
                       WHERE f.station_id = ANY(@st) AND f.sold_at > now() - interval '90 days' AND f.volume > 0 AND f.payment_kind <> 'none'),
                 -- MATERIALIZED: computed once. Inlined, the planner (no statistics yet for a just connected station)
                 -- re-ran it for every sale: 30+ s and a 500 on prod (1.10.1).
                 reg AS MATERIALIZED (SELECT DISTINCT r.station_id, r.data->>'DocID' AS doc FROM records r
                         WHERE r.station_id = ANY(@st) AND r.dataset = 'rgCashes' AND r.data->>'DocTypeID' = '6' AND NOT r.deleted
                           AND r.updated_at > now() - interval '92 days' -- written at or after the sale: uses the (dataset, updated_at) index
                           AND r.data->>'Date' >= to_char(now() - interval '92 days', 'YYYY-MM-DD')),
                 k AS (SELECT s.station_id, s.payment_mode_id, count(*) AS paid_90d, count(reg.doc) AS in_cash_register
                       FROM s LEFT JOIN reg ON reg.station_id = s.station_id AND reg.doc = s.order_id::text GROUP BY 1, 2)
            SELECT m.station_id, sn.station, m.payment_mode_id, m.name, m.is_discount, m.manual, m.category, m.category_manual,
                   payment_category_guess(m.name) AS category_guess,
                   (SELECT count(*) FROM fuel_sales f WHERE f.station_id = m.station_id AND f.payment_mode_id = m.payment_mode_id
                      AND f.sold_at > now() - interval '30 days' AND f.volume > 0) AS sales_30d,
                   COALESCE(k.paid_90d, 0) AS paid_90d, COALESCE(k.in_cash_register, 0) AS in_cash_register
            FROM v_payment_mode_flags m JOIN v_station_names sn ON sn.station_id = m.station_id
            LEFT JOIN k ON k.station_id = m.station_id AND k.payment_mode_id = m.payment_mode_id
            WHERE m.station_id = ANY(@st) AND m.payment_mode_id > 0
            ORDER BY sn.station, m.name
            """, new { st }));
    }

    private static readonly string[] Categories = ["cash", "card", "contract", "fuel_card", "none"];

    /// <summary>
    /// Owner's marks of a payment mode. Body fields are optional: "is_discount" true/false/null and
    /// "category" cash|card|contract|fuel_card|none|null — null returns the field to the guess by name.
    /// </summary>
    private static async Task<IResult> SetPaymentMode(Guid station, long mode, System.Text.Json.Nodes.JsonObject body, ClaimsPrincipal user,
                                                      StationScope scope, UiDb db, Processing.RollupRefresher rollups)
    {
        if (!await scope.CanSeeAsync(user, station)) return Results.NotFound();
        var setDiscount = body.TryGetPropertyValue("is_discount", out var d);
        var setCategory = body.TryGetPropertyValue("category", out var cat);
        bool? discount = d?.GetValue<bool>();
        var category = cat?.GetValue<string>();
        if (category is not null && !Categories.Contains(category)) return Results.Problem(statusCode: 400, title: "category: cash | card | contract | fuel_card | none");
        if (!setDiscount && !setCategory) return Results.Problem(statusCode: 400, title: "is_discount или category");

        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await c.ExecuteAsync("""
            INSERT INTO payment_mode_settings (station_id, payment_mode_id, is_discount, category, updated_by)
            VALUES (@station, @mode, @discount, @category, @by)
            ON CONFLICT (station_id, payment_mode_id) DO UPDATE SET
                is_discount = CASE WHEN @setDiscount THEN EXCLUDED.is_discount ELSE payment_mode_settings.is_discount END,
                category = CASE WHEN @setCategory THEN EXCLUDED.category ELSE payment_mode_settings.category END,
                updated_by = EXCLUDED.updated_by, updated_at = now()
            """, new { station, mode, discount, category, setDiscount, setCategory, by = user.Identity?.Name }, tx);
        await c.ExecuteAsync("DELETE FROM payment_mode_settings WHERE station_id = @station AND payment_mode_id = @mode AND is_discount IS NULL AND category IS NULL",
            new { station, mode }, tx);
        // The category decides which sales lose VAT in 'net' organizations: recompute that station's margins.
        if (setCategory) await c.ExecuteAsync("INSERT INTO rollup_dirty (station_id, kind) VALUES (@station, 'all')", new { station }, tx);
        await tx.CommitAsync();
        if (setCategory) rollups.Notify();
        await Audit.WriteAsync(db, user, "payment_mode_updated", $"station:{station}/payment_mode:{mode}", new { is_discount = discount, category, setDiscount, setCategory });
        return Results.NoContent();
    }

    private static async Task<IResult> List(NpgsqlDataSource db)
    {
        await using var c = await db.OpenConnectionAsync();
        var orgs = await c.QueryAsync<OrgRow>("""
            SELECT o.id, o.name, o.is_active AS IsActive, o.created_at AS CreatedAt,
                   (SELECT count(*) FROM stations s WHERE s.organization_id = o.id) AS Stations,
                   (SELECT count(*) FROM users u WHERE u.organization_id = o.id) AS Users,
                   (SELECT count(*) FROM users u WHERE u.organization_id = o.id AND u.role = 'owner' AND u.is_active) AS Owners
            FROM organizations o ORDER BY o.name
            """);
        var stations = (await c.QueryAsync<(Guid Id, string Name, Guid? Org)>(
            "SELECT id, name, organization_id FROM stations WHERE organization_id IS NOT NULL ORDER BY name")).ToLookup(s => s.Org);
        return Results.Ok(orgs.Select(o => new
        {
            id = o.Id, name = o.Name, is_active = o.IsActive, created_at = o.CreatedAt, stations = o.Stations, users = o.Users, owners = o.Owners,
            station_list = stations[o.Id].Select(s => new { id = s.Id, name = s.Name }),
        }));
    }

    private sealed record OrgRow(Guid Id, string Name, bool IsActive, DateTime CreatedAt, long Stations, long Users, long Owners);

    private static async Task<IResult> Create(OrganizationBody body, ClaimsPrincipal user, NpgsqlDataSource db)
    {
        var name = body.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100) return Results.Problem(statusCode: 400, title: "Название организации: от 1 до 100 символов");
        await using var c = await db.OpenConnectionAsync();
        var id = Guid.NewGuid();
        try
        {
            await c.ExecuteAsync("INSERT INTO organizations (id, name, name_normalized) VALUES (@id, @name, @norm)", new { id, name, norm = name.ToLowerInvariant() });
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Results.Problem(statusCode: 409, title: "Организация с таким названием уже есть");
        }
        await Audit.WritePlatformAsync(db, user, "organization_created", "organization:" + id, new { name });
        return Results.Ok(new { id });
    }

    /// <summary>Rename; disable = its users cannot sign in (stations keep sending data).</summary>
    private static async Task<IResult> Update(Guid id, OrganizationBody body, ClaimsPrincipal user, NpgsqlDataSource db, UserService users)
    {
        var name = body.Name?.Trim();
        if (name is { Length: 0 or > 100 }) return Results.Problem(statusCode: 400, title: "Название организации: от 1 до 100 символов");
        await using var c = await db.OpenConnectionAsync();
        try
        {
            var n = await c.ExecuteAsync("""
                UPDATE organizations SET name = COALESCE(@name, name), name_normalized = COALESCE(lower(@name), name_normalized), is_active = COALESCE(@IsActive, is_active)
                WHERE id = @id
                """, new { id, name, body.IsActive });
            if (n == 0) return Results.NotFound();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Results.Problem(statusCode: 409, title: "Организация с таким названием уже есть");
        }
        if (body.IsActive is not null) // disabled: sessions end at the next request; enabled again: no stale cache
            foreach (var u in await c.QueryAsync<Guid>("SELECT id FROM users WHERE organization_id = @id", new { id })) users.Forget(u);
        await Audit.WriteAsync(db, user.UserId(), user.Identity?.Name, "organization_updated", "organization:" + id, body, id);
        return Results.NoContent();
    }

    /// <summary>A station changes owner: its whole history moves with it; managers of the old organization lose it.</summary>
    private static async Task<IResult> MoveStation(Guid id, MoveStationBody body, ClaimsPrincipal user, NpgsqlDataSource db)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        if (!await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM organizations WHERE id = @o)", new { o = body.OrganizationId }, tx))
            return Results.Problem(statusCode: 400, title: "Организация не найдена");
        var from = await c.QuerySingleOrDefaultAsync<Guid?>("SELECT organization_id FROM stations WHERE id = @id FOR UPDATE", new { id }, tx);
        if (from is null && !await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM stations WHERE id = @id)", new { id }, tx)) return Results.NotFound();
        await c.ExecuteAsync("UPDATE stations SET organization_id = @o WHERE id = @id", new { id, o = body.OrganizationId }, tx);
        await c.ExecuteAsync("DELETE FROM user_stations us USING users u WHERE us.user_id = u.id AND us.station_id = @id AND u.organization_id IS DISTINCT FROM @o",
            new { id, o = body.OrganizationId }, tx);
        // The station's own costs go with it; margins are recomputed with the new organization's org-wide costs.
        await c.ExecuteAsync("UPDATE item_costs SET organization_id = @o WHERE station_id = @id", new { id, o = body.OrganizationId }, tx);
        await c.ExecuteAsync("INSERT INTO rollup_dirty (station_id, kind) VALUES (@id, 'all')", new { id }, tx);
        await tx.CommitAsync();
        await Audit.WriteAsync(db, user.UserId(), user.Identity?.Name, "station_moved", "station:" + id, new { from, to = body.OrganizationId }, body.OrganizationId);
        return Results.NoContent();
    }
}
