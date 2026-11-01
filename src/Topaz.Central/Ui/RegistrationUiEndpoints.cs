using System.Security.Claims;
using Dapper;
using Npgsql;
using Topaz.Central.Auth;

namespace Topaz.Central.Ui;

public sealed record ApproveBody(string? Code, Guid? StationId, string? StationName, Guid? OrganizationId = null);
public sealed record RejectBody(bool? BlockIp, string? Note);
public sealed record StationAccessBody(string? Action);

/// <summary>Connection requests of new agents (owners see them, only a superadmin decides) and station access control.</summary>
public static class RegistrationUiEndpoints
{
    public static void MapUiRegistrations(this RouteGroupBuilder ui)
    {
        // Requests belong to no organization yet (and show IPs and Topaz data): the superadmin's only.
        ui.MapGet("/registrations", List).RequireAuthorization(Policies.Super);
        var super = ui.MapGroup("").RequireAuthorization(Policies.Super);
        super.MapPost("/registrations/{id:guid}/approve", Approve);
        super.MapPost("/registrations/{id:guid}/reject", Reject);
        super.MapPost("/stations/{id:guid}/access", Access);
    }

    public static async Task<int> PendingCountAsync(NpgsqlDataSource db)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<int>("SELECT count(*) FROM station_registrations WHERE status = 'pending'");
    }

    private static async Task<IResult> List(ClaimsPrincipal user, NpgsqlDataSource db)
    {
        await using var c = await db.OpenConnectionAsync();
        const string columns = """
            g.id, g.station_name, g.machine_name, g.agent_version, g.os, g.rid, g.ip, g.topaz_info, g.status, g.created_at, g.last_poll_at, g.poll_count,
            g.decided_by, g.decided_at, g.note, g.station_id, COALESCE(NULLIF(s.display_name, ''), s.name) AS station,
            g.previous_station_id, COALESCE(NULLIF(p.display_name, ''), p.name) AS previous_station, p.status AS previous_station_status,
            EXISTS (SELECT 1 FROM registration_blocks b WHERE b.ip = g.ip AND b.until > now()) AS ip_blocked
            FROM station_registrations g
            LEFT JOIN stations s ON s.id = g.station_id
            LEFT JOIN stations p ON p.id = g.previous_station_id
            """;
        // jsonb comes back as text: the client parses topaz_info.
        var pending = await c.QueryAsync($"SELECT {columns} WHERE g.status = 'pending' ORDER BY g.created_at");
        var history = await c.QueryAsync($"SELECT {columns} WHERE g.status <> 'pending' AND g.created_at > now() - interval '30 days' ORDER BY COALESCE(g.decided_at, g.created_at) DESC LIMIT 200");
        var blocks = await c.QueryAsync("SELECT ip, until, blocked_by, reason FROM registration_blocks WHERE until > now() ORDER BY until DESC");
        return Results.Ok(new { can_decide = user.IsInRole(Roles.Superadmin), pending, history, blocks });
    }

    private static async Task<IResult> Approve(Guid id, ApproveBody body, ClaimsPrincipal user, Registrations regs, NpgsqlDataSource db)
    {
        var by = user.Identity?.Name ?? "?";
        var (result, stationId) = await regs.ApproveAsync(id, body.Code, body.StationId, body.StationName, by, body.OrganizationId);
        switch (result)
        {
            case Registrations.Decision.Ok:
                await Audit.WriteAsync(db, user.UserId(), user.Identity?.Name, "station_registration_approved", "registration:" + id,
                    new { station_id = stationId, existing = body.StationId is not null, body.StationName }, await OrganizationOfAsync(db, stationId));
                return Results.Ok(new { station_id = stationId });
            case Registrations.Decision.WrongCode:
                await Audit.WriteAsync(db, user, "station_registration_code_mismatch", "registration:" + id, null);
                return Results.Problem(statusCode: 400, title: "Код подтверждения не совпадает. Уточните код у того, кто устанавливал агент на АЗС.");
            case Registrations.Decision.NameTaken:
                return Results.Problem(statusCode: 409, title: "АЗС с таким названием уже есть. Выберите «Существующая АЗС» или укажите другое название.");
            case Registrations.Decision.NoOrganization:
                return Results.Problem(statusCode: 400, title: "Выберите организацию, к которой относится АЗС");
            case Registrations.Decision.NoStation:
                return Results.Problem(statusCode: 400, title: "Выбранная АЗС не найдена");
            case Registrations.Decision.NotPending:
                return Results.Problem(statusCode: 409, title: "Заявка уже обработана");
            default:
                return Results.NotFound();
        }
    }

    private static async Task<Guid?> OrganizationOfAsync(NpgsqlDataSource db, Guid? stationId)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<Guid?>("SELECT organization_id FROM stations WHERE id = @stationId", new { stationId });
    }

    private static async Task<IResult> Reject(Guid id, RejectBody body, ClaimsPrincipal user, Registrations regs, NpgsqlDataSource db)
    {
        var result = await regs.RejectAsync(id, body.BlockIp == true, body.Note, user.Identity?.Name ?? "?");
        if (result == Registrations.Decision.NotFound) return Results.NotFound();
        if (result == Registrations.Decision.NotPending) return Results.Problem(statusCode: 409, title: "Заявка уже обработана");
        await Audit.WritePlatformAsync(db, user, "station_registration_rejected", "registration:" + id, new { block_ip = body.BlockIp == true, body.Note });
        return Results.NoContent();
    }

    private static async Task<IResult> Access(Guid id, StationAccessBody body, ClaimsPrincipal user, Registrations regs, NpgsqlDataSource db)
    {
        if (body.Action is not ("block" or "unblock" or "reissue")) return Results.Problem(statusCode: 400, title: "action: block | unblock | reissue");
        if (!await regs.SetStationAccessAsync(id, body.Action))
            return body.Action == "unblock" ? Results.Problem(statusCode: 409, title: "У АЗС нет действующего доступа: перевыпустите его") : Results.NotFound();
        await Audit.WriteAsync(db, user.UserId(), user.Identity?.Name, "station_access_" + body.Action, "station:" + id, null, await OrganizationOfAsync(db, id));
        return Results.NoContent();
    }
}
