using System.Globalization;
using System.Security.Claims;
using Dapper;
using Npgsql;
using Topaz.Central.Processing;

namespace Topaz.Central.Ui;

/// <summary>A change of a site request; null fields stay as they are (the note: "" clears it).</summary>
public sealed record LeadUpdateBody(string? Stage, string? RefuseReason, string? RefuseComment, string? Note);

/// <summary>«Платформа → Заявки с сайта»: requests from the public site (platform data, table leads), superadmin only.</summary>
public static class LeadEndpoints
{
    public static readonly string[] Stages = ["new", "work", "connected", "refused"];
    public static readonly string[] RefuseReasons = ["price", "not_topaz", "no_answer", "spam", "other"];

    public static void MapUiLeads(this RouteGroupBuilder ui)
    {
        var super = ui.MapGroup("").RequireAuthorization(Policies.Super);
        super.MapGet("/leads", List);
        super.MapGet("/leads/summary", Summary);
        super.MapPatch("/leads/{id:long}", Update);
        super.MapDelete("/leads/{id:long}", Delete);
    }

    /// <summary>Requests nobody has called back yet (the menu badge).</summary>
    public static async Task<int> NewCountAsync(NpgsqlDataSource db)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<int>("SELECT count(*) FROM leads WHERE stage = 'new'");
    }

    private static async Task<IResult> List(NpgsqlDataSource db, int? limit)
    {
        await using var c = await db.OpenConnectionAsync();
        return Results.Ok(await c.QueryAsync("""
            SELECT id, created_at, name, contact, stations, city, comment, page, notified_at,
                   stage, stage_at, stage_by, refuse_reason, refuse_comment, note
            FROM leads ORDER BY id DESC LIMIT @limit
            """, new { limit = Math.Clamp(limit ?? 1000, 1, 5000) }));
    }

    /// <summary>Requests that came in a month (Moscow time), by their current stage, refusals by reason.</summary>
    private static async Task<IResult> Summary(NpgsqlDataSource db, string? month)
    {
        var m = DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed : DateTime.UtcNow.AddHours(3);
        var from = new DateTime(m.Year, m.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        await using var c = await db.OpenConnectionAsync();
        var rows = (await c.QueryAsync<(string Stage, string? Reason, int N)>("""
            SELECT stage, refuse_reason, count(*)::int FROM leads
            WHERE created_at >= @from::timestamp AT TIME ZONE 'Europe/Moscow'
              AND created_at < (@from::timestamp + interval '1 month') AT TIME ZONE 'Europe/Moscow'
            GROUP BY 1, 2
            """, new { from })).ToList();
        return Results.Ok(new
        {
            month = from.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            total = rows.Sum(r => r.N),
            stages = Stages.ToDictionary(s => s, s => rows.Where(r => r.Stage == s).Sum(r => r.N)),
            refuse_reasons = rows.Where(r => r.Stage == "refused").GroupBy(r => r.Reason ?? "other").ToDictionary(g => g.Key, g => g.Sum(r => r.N)),
        });
    }

    private static async Task<IResult> Update(long id, LeadUpdateBody body, ClaimsPrincipal user, NpgsqlDataSource db)
    {
        if (body.Stage is not null && !Stages.Contains(body.Stage)) return Results.Problem("Неизвестный этап", statusCode: 400);
        if (body.Stage == "refused" && (body.RefuseReason is null || !RefuseReasons.Contains(body.RefuseReason)))
            return Results.Problem("Укажите причину отказа", statusCode: 400);
        if (body.Stage == "refused" && body.RefuseReason == "other" && Leads.Clean(body.RefuseComment, 300) is null)
            return Results.Problem("Опишите причину отказа", statusCode: 400);

        await using var c = await db.OpenConnectionAsync();
        var n = await c.ExecuteAsync("""
            UPDATE leads SET
                stage          = COALESCE(@stage, stage),
                stage_at       = CASE WHEN @stage IS NOT NULL AND @stage <> stage THEN now() ELSE stage_at END,
                stage_by       = CASE WHEN @stage IS NOT NULL AND @stage <> stage THEN @login ELSE stage_by END,
                refuse_reason  = CASE WHEN @stage IS NULL THEN refuse_reason WHEN @stage = 'refused' THEN @reason END,
                refuse_comment = CASE WHEN @stage IS NULL THEN refuse_comment WHEN @stage = 'refused' THEN @reasonComment END,
                note           = CASE WHEN @note IS NULL THEN note ELSE NULLIF(@note, '') END
            WHERE id = @id
            """, new
        {
            id, stage = body.Stage, login = user.Identity?.Name, reason = body.RefuseReason,
            reasonComment = Leads.Clean(body.RefuseComment, 300), note = body.Note is null ? null : Leads.Clean(body.Note, 2000) ?? "",
        });
        if (n == 0) return Results.NotFound();
        // The note may hold personal details: the log keeps what changed, not the text.
        await Audit.WritePlatformAsync(db, user, "lead_updated", "lead:" + id,
            new { stage = body.Stage, refuse_reason = body.Stage == "refused" ? body.RefuseReason : null, note_changed = body.Note is not null });
        return Results.NoContent();
    }

    /// <summary>Spam, or a person asking to erase their data (152-ФЗ): the row is gone; the log keeps only its number.</summary>
    private static async Task<IResult> Delete(long id, ClaimsPrincipal user, NpgsqlDataSource db)
    {
        await using var c = await db.OpenConnectionAsync();
        if (await c.ExecuteAsync("DELETE FROM leads WHERE id = @id", new { id }) == 0) return Results.NotFound();
        await Audit.WritePlatformAsync(db, user, "lead_deleted", "lead:" + id, null);
        return Results.NoContent();
    }
}
