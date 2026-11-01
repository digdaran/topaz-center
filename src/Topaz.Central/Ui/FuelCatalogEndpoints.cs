using System.Security.Claims;
using Dapper;
using Npgsql;

namespace Topaz.Central.Ui;

public sealed record FuelTypeCreate(string? Name, int? Sort);
public sealed record FuelTypeUpdate(string? Name, int? Sort, bool? IsActive);
public sealed record FuelMapBody(string? FuelType);
public sealed record FuelConfirmBody(Guid? StationId, FuelRef[]? Fuels);
public sealed record FuelRef(Guid StationId, long FuelId);

/// <summary>
/// Reference catalog of fuel types (fuel_types) and the mapping of station fuels to it (station_fuel_map).
/// A station fuel without a mapping row is "not confirmed"; reports use its suggested type meanwhile (v_station_fuels).
/// </summary>
public static class FuelCatalogEndpoints
{
    public const string Fallback = "OTHER";

    public static void MapUiFuelCatalog(this RouteGroupBuilder ui)
    {
        ui.MapGet("/catalog/fuel-types", ListTypes);
        ui.MapGet("/catalog/fuels", ListFuels);
        // The reference list is shared by all organizations: the superadmin keeps it.
        var super = ui.MapGroup("").RequireAuthorization(Policies.Super);
        super.MapPost("/catalog/fuel-types", CreateType);
        super.MapPut("/catalog/fuel-types/{code}", UpdateType);
        super.MapDelete("/catalog/fuel-types/{code}", DeleteType);
        // Each owner confirms the mapping of the own stations.
        var owner = ui.MapGroup("").RequireAuthorization(Policies.Owner);
        owner.MapPut("/catalog/fuels/{station:guid}/{fuel:long}", SetMapping);
        owner.MapPost("/catalog/fuels/confirm", ConfirmSuggestions);
    }

    /// <summary>Fuels that still need an owner's decision, visible to this user.</summary>
    public static Task<int> UnconfirmedCountAsync(NpgsqlConnection c, Guid[] stations) =>
        c.ExecuteScalarAsync<int>("SELECT count(*) FROM v_station_fuels WHERE station_id = ANY(@stations) AND needs_confirmation", new { stations });

    private static async Task<IResult> ListTypes(ClaimsPrincipal user, StationScope scope, UiDb db)
    {
        var st = await scope.AllowedAsync(user);
        await using var c = await db.OpenConnectionAsync();
        var rows = await c.QueryAsync("""
            SELECT ft.code, ft.name, ft.sort, ft.is_active,
                   (SELECT count(*) FROM v_station_fuels f WHERE f.fuel_type = ft.code AND f.manual AND f.station_id = ANY(@st)) AS confirmed,
                   (SELECT count(*) FROM v_station_fuels f WHERE f.fuel_type = ft.code AND f.needs_confirmation AND f.station_id = ANY(@st)) AS suggested,
                   ft.code = ANY(@used) AS in_use
            FROM fuel_types ft ORDER BY ft.sort, ft.name
            """, new { st, used = await UsedTypesAsync(db) });
        return Results.Ok(rows);
    }

    private static async Task<IResult> ListFuels(HttpContext ctx, StationScope scope, UiDb db, Guid? station, string? unconfirmed)
    {
        var st = await scope.AllowedAsync(ctx.User);
        if (station is { } one) st = st.Contains(one) ? [one] : [];
        await using var c = await db.OpenConnectionAsync();
        var types = await c.QueryAsync("SELECT code, name, is_active FROM fuel_types ORDER BY sort, name");
        var fuels = await c.QueryAsync("""
            SELECT f.station_id, sn.station, f.fuel_id, f.fuel_name, f.fuel_type, f.manual AS confirmed, f.is_deleted,
                   f.suggested_type, f.suggestion_source, sf.station AS suggested_from, f.needs_confirmation,
                   m.confirmed_by, m.confirmed_at
            FROM v_station_fuels f
            JOIN v_station_names sn ON sn.station_id = f.station_id
            LEFT JOIN v_station_names sf ON sf.station_id = f.suggested_from
            LEFT JOIN station_fuel_map m ON m.station_id = f.station_id AND m.fuel_id = f.fuel_id
            WHERE f.station_id = ANY(@st) AND f.fuel_name NOT LIKE '<%' AND (NOT @onlyUnconfirmed OR f.needs_confirmation)
            ORDER BY sn.station, f.is_deleted, f.fuel_name
            """, new { st, onlyUnconfirmed = unconfirmed is "1" or "true" });
        return Results.Ok(new { types, fuels });
    }

    private static async Task<IResult> CreateType(FuelTypeCreate body, ClaimsPrincipal user, UiDb db)
    {
        var name = body.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 60) return Results.Problem(statusCode: 400, title: "Укажите название (до 60 символов)");
        await using var c = await db.OpenConnectionAsync();
        if (await NameTakenAsync(c, name, null)) return Results.Problem(statusCode: 409, title: $"Вид «{name}» уже есть в справочнике");
        var row = await c.QuerySingleAsync("""
            INSERT INTO fuel_types (code, name, sort)
            VALUES ('F' || nextval('fuel_type_code_seq'), @name,
                    COALESCE(@sort, (SELECT COALESCE(max(sort), 0) + 1 FROM fuel_types WHERE code <> @fallback)))
            RETURNING code, name, sort, is_active
            """, new { name, sort = body.Sort, fallback = Fallback });
        await Audit.WritePlatformAsync(db.System, user, "fuel_type_created", "fuel_type:" + (string)row.code, new { name });
        return Results.Ok(row);
    }

    private static async Task<IResult> UpdateType(string code, FuelTypeUpdate body, ClaimsPrincipal user, UiDb db)
    {
        var name = body.Name?.Trim();
        if (name is { Length: 0 or > 60 }) return Results.Problem(statusCode: 400, title: "Укажите название (до 60 символов)");
        if (code == Fallback && body.IsActive == false) return Results.Problem(statusCode: 400, title: "Вид «Прочее» нельзя скрыть: в него попадает нераспознанное топливо");
        await using var c = await db.OpenConnectionAsync();
        if (name is not null && await NameTakenAsync(c, name, code)) return Results.Problem(statusCode: 409, title: $"Вид «{name}» уже есть в справочнике");
        var n = await c.ExecuteAsync("""
            UPDATE fuel_types SET name = COALESCE(@name, name), sort = COALESCE(@sort, sort), is_active = COALESCE(@active, is_active)
            WHERE code = @code
            """, new { code, name, sort = body.Sort, active = body.IsActive });
        if (n == 0) return Results.NotFound();
        await Audit.WritePlatformAsync(db.System, user, "fuel_type_updated", "fuel_type:" + code, body);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteType(string code, ClaimsPrincipal user, UiDb db)
    {
        if (code == Fallback) return Results.Problem(statusCode: 400, title: "Вид «Прочее» удалить нельзя");
        await using var c = await db.OpenConnectionAsync();
        if ((await UsedTypesAsync(db)).Contains(code)) return Results.Problem(statusCode: 409, title: "Вид уже используется в сопоставлениях или планах — скройте его вместо удаления");
        var n = await c.ExecuteAsync("DELETE FROM fuel_types WHERE code = @code", new { code });
        if (n == 0) return Results.NotFound();
        await Audit.WritePlatformAsync(db.System, user, "fuel_type_deleted", "fuel_type:" + code, null);
        return Results.NoContent();
    }

    /// <summary>Confirms a station fuel as the given type; an empty type removes the confirmation (back to the suggestion).</summary>
    private static async Task<IResult> SetMapping(Guid station, long fuel, FuelMapBody body, ClaimsPrincipal user, UiDb db, StationScope scope)
    {
        if (!await scope.CanSeeAsync(user, station)) return Results.NotFound();
        await using var c = await db.OpenConnectionAsync();
        if (string.IsNullOrEmpty(body.FuelType))
            await c.ExecuteAsync("DELETE FROM station_fuel_map WHERE station_id = @station AND fuel_id = @fuel", new { station, fuel });
        else
        {
            if (!await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM fuel_types WHERE code = @t)", new { t = body.FuelType }))
                return Results.Problem(statusCode: 400, title: "Неизвестный вид топлива");
            if (!await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM v_station_fuels WHERE station_id = @station AND fuel_id = @fuel)", new { station, fuel }))
                return Results.NotFound();
            await c.ExecuteAsync("""
                INSERT INTO station_fuel_map (station_id, fuel_id, fuel_type, confirmed_by) VALUES (@station, @fuel, @t, @by)
                ON CONFLICT (station_id, fuel_id) DO UPDATE SET fuel_type = EXCLUDED.fuel_type, confirmed_by = EXCLUDED.confirmed_by, confirmed_at = now()
                """, new { station, fuel, t = body.FuelType, by = user.Identity?.Name });
        }
        await Audit.WriteAsync(db, user, "fuel_type_mapped", $"station:{station}/fuel:{fuel}", body);
        return Results.NoContent();
    }

    /// <summary>Accepts the suggested types: of all unconfirmed fuels, of one station, or of the listed fuels.</summary>
    private static async Task<IResult> ConfirmSuggestions(FuelConfirmBody body, ClaimsPrincipal user, UiDb db, StationScope scope)
    {
        var st = await scope.AllowedAsync(user);
        if (body.StationId is { } one && !st.Contains(one)) return Results.NotFound();
        var fuels = body.Fuels ?? [];
        await using var c = await db.OpenConnectionAsync();
        var n = await c.ExecuteAsync("""
            INSERT INTO station_fuel_map (station_id, fuel_id, fuel_type, confirmed_by)
            SELECT f.station_id, f.fuel_id, f.suggested_type, @by FROM v_station_fuels f
            WHERE f.needs_confirmation AND f.station_id = ANY(@st)
              AND (@station::uuid IS NULL OR f.station_id = @station)
              AND (cardinality(@ids) = 0 OR (f.station_id::text || '/' || f.fuel_id) = ANY(@ids))
            ON CONFLICT (station_id, fuel_id) DO NOTHING
            """, new { by = user.Identity?.Name, st, station = body.StationId, ids = fuels.Select(f => $"{f.StationId}/{f.FuelId}").ToArray() });
        await Audit.WriteAsync(db, user, "fuel_types_confirmed", body.StationId is { } s ? "station:" + s : "all", new { count = n, fuels = fuels.Length });
        return Results.Ok(new { confirmed = n });
    }

    /// <summary>Types used by any organization (unrestricted pool: the shared list must not lose a type someone uses).</summary>
    private static async Task<string[]> UsedTypesAsync(UiDb db)
    {
        await using var c = await db.System.OpenConnectionAsync();
        return (await c.QueryAsync<string>("""
            SELECT fuel_type FROM station_fuel_map UNION SELECT fuel_type FROM sales_plans WHERE fuel_type <> ''
            UNION SELECT fuel_type FROM v_station_fuels UNION SELECT @fallback
            """, new { fallback = Fallback })).ToArray();
    }

    private static Task<bool> NameTakenAsync(NpgsqlConnection c, string name, string? exceptCode) =>
        c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM fuel_types WHERE lower(name) = lower(@name) AND code IS DISTINCT FROM @exceptCode)", new { name, exceptCode });
}
