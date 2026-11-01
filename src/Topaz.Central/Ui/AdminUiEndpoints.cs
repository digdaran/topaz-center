using System.Security.Claims;
using Dapper;
using Npgsql;

namespace Topaz.Central.Ui;

public sealed record StationUpdate(string? DisplayName, string? TimeZone, string? Region, string? GroupName, string? Address);
public sealed record UserCreate(string Login, string Name, string Role, string Password, Guid[]? Stations);
public sealed record UserUpdate(string? Name, string? Role, bool? IsActive, Guid[]? Stations, string? Password);

/// <summary>Meta for filters, station monitoring/settings, user management, audit log.</summary>
public static class AdminUiEndpoints
{
    public static void MapUiAdmin(this RouteGroupBuilder ui)
    {
        ui.MapGet("/meta", Meta);
        ui.MapGet("/stations", Stations);
        ui.MapPatch("/stations/{id:guid}", UpdateStation).RequireAuthorization(Policies.Owner);

        var owner = ui.MapGroup("").RequireAuthorization(Policies.Owner);
        owner.MapGet("/users", ListUsers);
        owner.MapPost("/users", CreateUser);
        owner.MapPatch("/users/{id:guid}", UpdateUser);
        owner.MapGet("/audit", (NpgsqlDataSource db, int? limit) => QueryList(db,
            "SELECT id, user_login, action, entity, details, at FROM audit_log ORDER BY id DESC LIMIT @limit", new { limit = Math.Clamp(limit ?? 200, 1, 2000) }));
    }

    private static async Task<IResult> QueryList(NpgsqlDataSource db, string sql, object args)
    {
        await using var c = await db.OpenConnectionAsync();
        return Results.Ok(await c.QueryAsync(sql, args));
    }

    private static async Task<IResult> Meta(ClaimsPrincipal user, StationScope scope, NpgsqlDataSource db)
    {
        var st = await scope.AllowedAsync(user);
        await using var c = await db.OpenConnectionAsync();
        var stations = await c.QueryAsync("""
            SELECT sn.station_id AS id, sn.station AS name, sn.time_zone, sn.region, sn.group_name
            FROM v_station_names sn WHERE sn.station_id = ANY(@st) ORDER BY sn.station
            """, new { st });
        var fuels = await c.QueryAsync<string>("SELECT DISTINCT name FROM v_fuels WHERE station_id = ANY(@st) AND NOT is_deleted AND name NOT LIKE '<%' ORDER BY 1", new { st });
        var groups = await c.QueryAsync<string>("SELECT DISTINCT name FROM v_item_groups WHERE station_id = ANY(@st) AND name NOT LIKE '%<%' ORDER BY 1", new { st });
        var paymentModes = await c.QueryAsync<string>("SELECT DISTINCT name FROM v_payment_modes WHERE station_id = ANY(@st) ORDER BY 1", new { st });
        var range = await c.QuerySingleAsync("SELECT min(sold_at) AS first_sale, max(sold_at) AS last_sale FROM fuel_sales WHERE station_id = ANY(@st)", new { st });
        // Reference fuel types: names for every code, plus which ones the visible stations actually sell (for the filter).
        var fuelTypes = await c.QueryAsync("""
            SELECT ft.code, ft.name, ft.is_active, EXISTS (SELECT 1 FROM v_station_fuels f WHERE f.fuel_type = ft.code AND f.station_id = ANY(@st) AND NOT f.is_deleted) AS present
            FROM fuel_types ft ORDER BY ft.sort, ft.name
            """, new { st });
        var unmapped = await FuelCatalogEndpoints.UnconfirmedCountAsync(c, st);
        return Results.Ok(new { stations, fuels, fuel_types = fuelTypes, unmapped_fuels = unmapped, item_groups = groups, payment_modes = paymentModes, data_range = range });
    }

    private static async Task<IResult> Stations(ClaimsPrincipal user, StationScope scope, NpgsqlDataSource db)
    {
        var st = await scope.AllowedAsync(user);
        await using var c = await db.OpenConnectionAsync();
        var rows = await c.QueryAsync("""
            SELECT v.id, s.name AS topaz_name, COALESCE(NULLIF(s.display_name, ''), s.name) AS name, s.display_name, s.time_zone, s.time_zone_manual,
                   s.region, s.group_name, s.address, v.status, v.agent_version, v.machine_name, v.last_seen_at,
                   extract(epoch FROM v.silent_for)::bigint AS silent_seconds, v.agent_queue_pending, v.agent_queue_dead, v.topaz_db_ok,
                   v.last_collect_error, v.server_backlog, v.server_failed, s.last_heartbeat->>'topaz_db_version' AS topaz_db_version,
                   (s.last_heartbeat->>'last_session_id')::bigint AS last_session_id, (s.last_heartbeat->>'last_session_open')::boolean AS last_session_open,
                   s.created_at,
                   (SELECT max(sold_at) FROM fuel_sales fs WHERE fs.station_id = s.id) AS last_sale_at
            FROM v_station_status v JOIN stations s ON s.id = v.id
            WHERE v.id = ANY(@st) ORDER BY name
            """, new { st });
        return Results.Ok(rows);
    }

    private static async Task<IResult> UpdateStation(Guid id, StationUpdate body, ClaimsPrincipal user, NpgsqlDataSource db, Topaz.Central.Processing.RollupRefresher rollups)
    {
        await using var c = await db.OpenConnectionAsync();
        if (body.TimeZone is { Length: > 0 } tz && !await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM pg_timezone_names WHERE name = @tz)", new { tz }))
            return Results.Problem(statusCode: 400, title: $"Неизвестный часовой пояс {tz}");
        var n = await c.ExecuteAsync("""
            UPDATE stations SET
                display_name = COALESCE(@DisplayName, display_name),
                time_zone = CASE WHEN COALESCE(@TimeZone, '') <> '' THEN @TimeZone ELSE time_zone END,
                time_zone_manual = time_zone_manual OR COALESCE(@TimeZone, '') <> '',
                region = COALESCE(@Region, region), group_name = COALESCE(@GroupName, group_name), address = COALESCE(@Address, address)
            WHERE id = @id
            """, new { id, body.DisplayName, body.TimeZone, body.Region, body.GroupName, body.Address });
        if (n == 0) return Results.NotFound();
        if (!string.IsNullOrEmpty(body.TimeZone))
            await c.ExecuteAsync("INSERT INTO rollup_dirty (station_id, kind) VALUES (@id, 'all')", new { id }); // local days shift
        rollups.Notify();
        await Audit.WriteAsync(db, user, "station_updated", "station:" + id, body);
        return Results.NoContent();
    }

    private static async Task<IResult> ListUsers(NpgsqlDataSource db)
    {
        await using var c = await db.OpenConnectionAsync();
        var rows = await c.QueryAsync("""
            SELECT u.id, u.login, u.name, u.role, u.is_active, u.created_at, u.last_login_at, u.locked_until,
                   COALESCE(array_agg(us.station_id) FILTER (WHERE us.station_id IS NOT NULL), '{}') AS stations
            FROM users u LEFT JOIN user_stations us ON us.user_id = u.id
            GROUP BY u.id ORDER BY u.login
            """);
        return Results.Ok(rows);
    }

    private static async Task<IResult> CreateUser(UserCreate body, ClaimsPrincipal user, UserService users, NpgsqlDataSource db)
    {
        if (string.IsNullOrWhiteSpace(body.Login) || body.Login.Trim().Length > 64) return Results.Problem(statusCode: 400, title: "Логин: от 1 до 64 символов");
        if (!Roles.All.Contains(body.Role)) return Results.Problem(statusCode: 400, title: "Роль: owner, manager или viewer");
        if (UserService.ValidatePassword(body.Password) is { } err) return Results.Problem(statusCode: 400, title: err);
        try
        {
            var id = await users.CreateAsync(body.Login, string.IsNullOrWhiteSpace(body.Name) ? body.Login : body.Name, body.Role, body.Password, body.Stations);
            await Audit.WriteAsync(db, user, "user_created", "user:" + id, new { body.Login, body.Role, body.Stations });
            return Results.Ok(new { id });
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Results.Problem(statusCode: 409, title: "Такой логин уже есть");
        }
    }

    private static async Task<IResult> UpdateUser(Guid id, UserUpdate body, ClaimsPrincipal user, UserService users, StationScope scope, NpgsqlDataSource db)
    {
        if (body.Role is not null && !Roles.All.Contains(body.Role)) return Results.Problem(statusCode: 400, title: "Роль: owner, manager или viewer");
        if (body.Password is not null && UserService.ValidatePassword(body.Password) is { } err) return Results.Problem(statusCode: 400, title: err);
        if (id == user.UserId() && (body.IsActive == false || (body.Role is not null && body.Role != Roles.Owner)))
            return Results.Problem(statusCode: 400, title: "Нельзя отключить себя или снять с себя роль владельца");

        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var n = await c.ExecuteAsync("""
            UPDATE users SET name = COALESCE(@Name, name), role = COALESCE(@Role, role), is_active = COALESCE(@IsActive, is_active),
                password_hash = COALESCE(@hash, password_hash), failed_logins = CASE WHEN @hash IS NULL THEN failed_logins ELSE 0 END,
                locked_until = CASE WHEN @hash IS NULL THEN locked_until ELSE NULL END,
                security_stamp = @stamp
            WHERE id = @id
            """, new { id, body.Name, body.Role, body.IsActive, hash = body.Password is null ? null : UserService.HashPassword(body.Password), stamp = Guid.NewGuid() }, tx);
        if (n == 0) return Results.NotFound();
        if (body.Stations is not null)
        {
            await c.ExecuteAsync("DELETE FROM user_stations WHERE user_id = @id", new { id }, tx);
            foreach (var s in body.Stations.Distinct())
                await c.ExecuteAsync("INSERT INTO user_stations (user_id, station_id) SELECT @id, @s WHERE EXISTS (SELECT 1 FROM stations WHERE id = @s)", new { id, s }, tx);
        }
        await tx.CommitAsync();
        users.Forget(id);
        scope.Forget(id);
        await Audit.WriteAsync(db, user, "user_updated", "user:" + id, new { body.Name, body.Role, body.IsActive, body.Stations, password_changed = body.Password is not null });
        return Results.NoContent();
    }
}
