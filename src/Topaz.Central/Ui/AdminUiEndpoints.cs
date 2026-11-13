using System.Security.Claims;
using Dapper;
using Npgsql;

namespace Topaz.Central.Ui;

public sealed record StationUpdate(string? DisplayName, string? TimeZone, string? Region, string? GroupName, string? Address);
public sealed record UserCreate(string Login, string Name, string Role, string Password, Guid[]? Stations, Guid? OrganizationId = null);
public sealed record UserUpdate(string? Name, string? Role, bool? IsActive, Guid[]? Stations, string? Password, Guid? OrganizationId = null, bool? ResetTotp = null);

/// <summary>Meta for filters, station monitoring/settings, user management, audit log.</summary>
public static class AdminUiEndpoints
{
    public static void MapUiAdmin(this RouteGroupBuilder ui)
    {
        ui.MapGet("/meta", Meta);
        ui.MapGet("/stations", Stations);
        ui.MapPatch("/stations/{id:guid}", UpdateStation).RequireAuthorization(Policies.Owner);

        // An owner manages the users of the own organization; a superadmin manages everyone.
        var owner = ui.MapGroup("").RequireAuthorization(Policies.Owner);
        owner.MapGet("/users", ListUsers);
        owner.MapPost("/users", CreateUser);
        owner.MapPatch("/users/{id:guid}", UpdateUser);
        owner.MapGet("/audit", (ClaimsPrincipal user, UiDb db, int? limit) => QueryList(db, """
            SELECT a.id, a.user_login, a.action, a.entity, a.details, a.at, o.name AS organization
            FROM audit_log a LEFT JOIN organizations o ON o.id = a.organization_id
            WHERE @super OR a.organization_id = @org ORDER BY a.id DESC LIMIT @limit
            """, new { super = user.IsInRole(Roles.Superadmin), org = user.OrganizationId(), limit = Math.Clamp(limit ?? 200, 1, 2000) }));
    }

    private static async Task<IResult> QueryList(UiDb db, string sql, object args)
    {
        await using var c = await db.OpenConnectionAsync();
        return Results.Ok(await c.QueryAsync(sql, args));
    }

    private static async Task<IResult> Meta(ClaimsPrincipal user, StationScope scope, UiDb db)
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
        // Connection requests belong to no organization yet: the superadmin's.
        var pendingRegistrations = user.IsInRole(Roles.Superadmin) ? await RegistrationUiEndpoints.PendingCountAsync(db.System) : 0;
        // Receipts waiting for a purchase price (costs are not shown to viewers).
        var unpriced = user.IsInRole(Roles.Viewer) ? 0 : await c.ExecuteScalarAsync<long>(MoneyEndpoints.UnpricedReceiptsSql, new { st });
        return Results.Ok(new { stations, fuels, fuel_types = fuelTypes, unmapped_fuels = unmapped, pending_registrations = pendingRegistrations, unpriced_receipts = unpriced, item_groups = groups, payment_modes = paymentModes, data_range = range });
    }

    private static async Task<IResult> Stations(ClaimsPrincipal user, StationScope scope, UiDb db)
    {
        var st = await scope.AllowedAsync(user);
        await using var c = await db.OpenConnectionAsync();
        var rows = await c.QueryAsync("""
            SELECT v.id, s.name AS topaz_name, COALESCE(NULLIF(s.display_name, ''), s.name) AS name, s.display_name, s.time_zone, s.time_zone_manual,
                   s.region, s.group_name, s.address, v.status, v.agent_version, v.machine_name, v.last_seen_at,
                   extract(epoch FROM v.silent_for)::bigint AS silent_seconds, v.agent_queue_pending, v.agent_queue_dead, v.topaz_db_ok,
                   v.last_collect_error, v.server_backlog, v.server_failed, s.last_heartbeat->>'topaz_db_version' AS topaz_db_version,
                   (s.last_heartbeat->>'last_session_id')::bigint AS last_session_id, (s.last_heartbeat->>'last_session_open')::boolean AS last_session_open,
                   s.created_at, s.agent_rid, s.update_group,
                   s.clock_skew_seconds, s.host_checked_at, d.db_disk_free, d.db_disk_total, d.db_size, d.sys_disk_free, d.sys_disk_total,
                   round(d.db_growth_per_day * 7) AS db_growth_per_week,
                   CASE WHEN d.free_drop_per_day > 0
                        THEN ceil(least(COALESCE(d.db_disk_free, d.sys_disk_free), COALESCE(d.sys_disk_free, d.db_disk_free)) / d.free_drop_per_day) END AS days_until_full,
                   (s.last_heartbeat->'host'->>'uptime_seconds')::bigint AS uptime_seconds, s.last_heartbeat->'host'->>'os' AS os,
                   s.last_heartbeat->'host'->>'windows_time_zone' AS windows_time_zone,
                   least(COALESCE(d.db_disk_free, d.sys_disk_free), COALESCE(d.sys_disk_free, d.db_disk_free))
                       < (SELECT threshold FROM alert_rules WHERE code = 'disk_low') * 1073741824 AS disk_low,
                   abs(s.clock_skew_seconds) > (SELECT threshold FROM alert_rules WHERE code = 'clock_skew') * 60 AS clock_off,
                   (SELECT max(sold_at) FROM fuel_sales fs WHERE fs.station_id = s.id) AS last_sale_at
            FROM v_station_status v JOIN stations s ON s.id = v.id LEFT JOIN v_station_disk d ON d.station_id = s.id
            WHERE v.id = ANY(@st) ORDER BY name
            """, new { st });
        return Results.Ok(rows);
    }

    private static async Task<IResult> UpdateStation(Guid id, StationUpdate body, ClaimsPrincipal user, UiDb db, StationScope scope, Topaz.Central.Processing.RollupRefresher rollups)
    {
        if (!await scope.CanSeeAsync(user, id)) return Results.NotFound();
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

    private static async Task<IResult> ListUsers(ClaimsPrincipal user, UiDb db)
    {
        await using var c = await db.OpenConnectionAsync();
        var rows = await c.QueryAsync("""
            SELECT u.id, u.login, u.name, u.role, u.is_active, u.created_at, u.last_login_at, u.locked_until, u.organization_id, o.name AS organization,
                   u.totp_secret IS NOT NULL AS totp_enabled,
                   (SELECT count(*)::int FROM user_passkeys p WHERE p.user_id = u.id) AS passkeys,
                   COALESCE(array_agg(us.station_id) FILTER (WHERE us.station_id IS NOT NULL), '{}') AS stations
            FROM users u LEFT JOIN user_stations us ON us.user_id = u.id LEFT JOIN organizations o ON o.id = u.organization_id
            WHERE @super OR u.organization_id = @org
            GROUP BY u.id, o.name ORDER BY o.name NULLS FIRST, u.login
            """, new { super = user.IsInRole(Roles.Superadmin), org = user.OrganizationId() });
        return Results.Ok(rows);
    }

    /// <summary>Who may give which role: an owner only within the own organization and never a superadmin.</summary>
    private static string? CheckRole(ClaimsPrincipal user, string role) =>
        !Roles.All.Contains(role) ? "Роль: superadmin, owner, manager или viewer"
        : role == Roles.Superadmin && !user.IsInRole(Roles.Superadmin) ? "Назначить суперадминистратора может только суперадминистратор"
        : null;

    /// <summary>Stations given to a user must belong to that user's organization.</summary>
    private static async Task<bool> StationsBelongAsync(NpgsqlConnection c, Guid[]? stations, Guid? org) =>
        stations is not { Length: > 0 } || org is not null && await c.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM stations WHERE id = ANY(@stations) AND organization_id = @org", new { stations, org }) == stations.Distinct().Count();

    private static async Task<IResult> CreateUser(UserCreate body, ClaimsPrincipal user, UserService users, UiDb db)
    {
        if (string.IsNullOrWhiteSpace(body.Login) || body.Login.Trim().Length > 64) return Results.Problem(statusCode: 400, title: "Логин: от 1 до 64 символов");
        if (CheckRole(user, body.Role) is { } roleError) return Results.Problem(statusCode: 400, title: roleError);
        if (UserService.ValidatePassword(body.Password) is { } err) return Results.Problem(statusCode: 400, title: err);
        var org = body.Role == Roles.Superadmin ? null : user.IsInRole(Roles.Superadmin) ? body.OrganizationId ?? user.OrganizationId() : user.OrganizationId();
        if (body.Role != Roles.Superadmin && org is null) return Results.Problem(statusCode: 400, title: "Укажите организацию");
        await using (var c = await db.System.OpenConnectionAsync())
        {
            if (org is not null && !await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM organizations WHERE id = @org)", new { org }))
                return Results.Problem(statusCode: 400, title: "Организация не найдена");
            if (!await StationsBelongAsync(c, body.Stations, org)) return Results.Problem(statusCode: 400, title: "АЗС должны принадлежать организации пользователя");
        }
        try
        {
            var id = await users.CreateAsync(body.Login, string.IsNullOrWhiteSpace(body.Name) ? body.Login : body.Name, body.Role, body.Password, body.Stations, org);
            await Audit.WriteAsync(db.System, user.UserId(), user.Identity?.Name, "user_created", "user:" + id, new { body.Login, body.Role, body.Stations }, org);
            return Results.Ok(new { id });
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Results.Problem(statusCode: 409, title: "Такой логин уже есть");
        }
    }

    private static async Task<IResult> UpdateUser(Guid id, UserUpdate body, ClaimsPrincipal user, UserService users, StationScope scope, UiDb db)
    {
        if (body.Role is not null && CheckRole(user, body.Role) is { } roleError) return Results.Problem(statusCode: 400, title: roleError);
        if (body.Password is not null && UserService.ValidatePassword(body.Password) is { } err) return Results.Problem(statusCode: 400, title: err);
        if (id == user.UserId() && body.IsActive == false) return Results.Problem(statusCode: 400, title: "Нельзя отключить себя");

        var super = user.IsInRole(Roles.Superadmin);
        await using var c = await db.System.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var target = await c.QuerySingleOrDefaultAsync<(string Role, Guid? Org)?>("SELECT role, organization_id FROM users WHERE id = @id FOR UPDATE", new { id }, tx);
        // An owner sees only the own organization's users, and never superadmins.
        if (target is null || (!super && (target.Value.Org != user.OrganizationId() || target.Value.Role == Roles.Superadmin))) return Results.NotFound();
        var newRole = body.Role ?? target.Value.Role;
        var newOrg = newRole == Roles.Superadmin ? null : super ? body.OrganizationId ?? target.Value.Org ?? user.OrganizationId() : target.Value.Org;
        if (newRole != Roles.Superadmin && newOrg is null) return Results.Problem(statusCode: 400, title: "Укажите организацию");
        if (!await StationsBelongAsync(c, body.Stations, newOrg)) return Results.Problem(statusCode: 400, title: "АЗС должны принадлежать организации пользователя");

        // There must always be an active superadmin: nobody else can connect stations or manage organizations.
        var losesSuper = target.Value.Role == Roles.Superadmin && (body.IsActive == false || newRole != Roles.Superadmin);
        if (losesSuper && !await c.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM users o WHERE o.role = 'superadmin' AND o.is_active AND o.id <> @id)", new { id }, tx))
            return Results.Problem(statusCode: 400, title: "Это последний суперадминистратор: сначала назначьте другого");

        await c.ExecuteAsync("""
            UPDATE users SET name = COALESCE(@Name, name), role = @newRole, organization_id = @newOrg, is_active = COALESCE(@IsActive, is_active),
                password_hash = COALESCE(@hash, password_hash), failed_logins = CASE WHEN @hash IS NULL THEN failed_logins ELSE 0 END,
                locked_until = CASE WHEN @hash IS NULL THEN locked_until ELSE NULL END,
                security_stamp = @stamp
            WHERE id = @id
            """, new { id, body.Name, newRole, newOrg, body.IsActive, hash = body.Password is null ? null : UserService.HashPassword(body.Password), stamp = Guid.NewGuid() }, tx);
        // Lost phone: the app's codes, the recovery codes and the passkeys stop working; the user sets them up again.
        if (body.ResetTotp == true)
            await c.ExecuteAsync(UserService.ResetSecondFactorsSql, new { id, stamp = Guid.NewGuid() }, tx);
        if (body.Stations is not null || newOrg != target.Value.Org)
        {
            await c.ExecuteAsync("DELETE FROM user_stations WHERE user_id = @id", new { id }, tx);
            foreach (var s in (body.Stations ?? []).Distinct())
                await c.ExecuteAsync("INSERT INTO user_stations (user_id, station_id) VALUES (@id, @s)", new { id, s }, tx);
        }
        if (newOrg != target.Value.Org)
        {
            // The own Telegram chat goes along: listed and managed in the new organization, not left with the old one
            // (its messages already follow the user's current stations); unused link codes of the old one are dropped.
            await c.ExecuteAsync("UPDATE notification_channels SET organization_id = @newOrg WHERE user_id = @id", new { id, newOrg }, tx);
            await c.ExecuteAsync("DELETE FROM telegram_link_codes WHERE user_id = @id AND used_at IS NULL", new { id }, tx);
        }
        await tx.CommitAsync();
        users.Forget(id);
        scope.Forget(id);
        await Audit.WriteAsync(db.System, user.UserId(), user.Identity?.Name, "user_updated", "user:" + id,
            new { body.Name, body.Role, body.IsActive, body.Stations, organization_id = newOrg, password_changed = body.Password is not null, totp_reset = body.ResetTotp == true }, newOrg);
        return Results.NoContent();
    }
}
