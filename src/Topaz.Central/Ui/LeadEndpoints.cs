using System.Security.Claims;
using Dapper;
using Npgsql;

namespace Topaz.Central.Ui;

public sealed record LeadStatusBody(bool Handled);

/// <summary>«Платформа → Заявки с сайта»: requests from the public site (platform data, table leads), superadmin only.</summary>
public static class LeadEndpoints
{
    public static void MapUiLeads(this RouteGroupBuilder ui)
    {
        var super = ui.MapGroup("").RequireAuthorization(Policies.Super);
        super.MapGet("/leads", List);
        super.MapPost("/leads/{id:long}/status", SetStatus);
    }

    /// <summary>Requests not marked as handled yet (the menu badge).</summary>
    public static async Task<int> NewCountAsync(NpgsqlDataSource db)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<int>("SELECT count(*) FROM leads WHERE handled_at IS NULL");
    }

    private static async Task<IResult> List(NpgsqlDataSource db, int? limit)
    {
        await using var c = await db.OpenConnectionAsync();
        return Results.Ok(await c.QueryAsync("""
            SELECT id, created_at, name, contact, stations, city, comment, page, notified_at, handled_at, handled_by
            FROM leads ORDER BY id DESC LIMIT @limit
            """, new { limit = Math.Clamp(limit ?? 500, 1, 5000) }));
    }

    private static async Task<IResult> SetStatus(long id, LeadStatusBody body, ClaimsPrincipal user, NpgsqlDataSource db)
    {
        await using var c = await db.OpenConnectionAsync();
        var n = await c.ExecuteAsync(body.Handled
            ? "UPDATE leads SET handled_at = now(), handled_by = @login WHERE id = @id AND handled_at IS NULL"
            : "UPDATE leads SET handled_at = NULL, handled_by = NULL WHERE id = @id",
            new { id, login = user.Identity?.Name });
        if (n == 0 && !await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM leads WHERE id = @id)", new { id })) return Results.NotFound();
        await Audit.WritePlatformAsync(db, user, body.Handled ? "lead_handled" : "lead_reopened", "lead:" + id, null);
        return Results.NoContent();
    }
}
