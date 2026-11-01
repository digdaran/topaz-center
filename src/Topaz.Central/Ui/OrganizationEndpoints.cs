using System.Security.Claims;
using Dapper;
using Npgsql;

namespace Topaz.Central.Ui;

public sealed record OrganizationBody(string? Name, bool? IsActive);
public sealed record MoveStationBody(Guid OrganizationId);

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
    }

    private static async Task<IResult> List(NpgsqlDataSource db)
    {
        await using var c = await db.OpenConnectionAsync();
        return Results.Ok(await c.QueryAsync("""
            SELECT o.id, o.name, o.is_active, o.created_at,
                   (SELECT count(*) FROM stations s WHERE s.organization_id = o.id) AS stations,
                   (SELECT count(*) FROM users u WHERE u.organization_id = o.id) AS users,
                   (SELECT count(*) FROM users u WHERE u.organization_id = o.id AND u.role = 'owner' AND u.is_active) AS owners
            FROM organizations o ORDER BY o.name
            """));
    }

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
        await tx.CommitAsync();
        await Audit.WriteAsync(db, user.UserId(), user.Identity?.Name, "station_moved", "station:" + id, new { from, to = body.OrganizationId }, body.OrganizationId);
        return Results.NoContent();
    }
}
