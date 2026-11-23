using System.Globalization;
using System.Security.Cryptography;
using System.Security.Claims;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Topaz.Central.Processing;

namespace Topaz.Central.Ui;

/// <summary>A change of a site request; null fields stay as they are (the note: "" clears it).</summary>
public sealed record LeadUpdateBody(string? Stage, string? RefuseReason, string? RefuseComment, string? Note);

/// <summary>«Создать клиента»: a new organization with its owner, or the request attached to an existing organization.</summary>
public sealed record LeadClientBody(string? Mode, Guid? OrganizationId, string? OrganizationName, string? OwnerName, string? OwnerLogin);

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
        super.MapPost("/leads/{id:long}/client", CreateClient);
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
            SELECT l.id, l.created_at, l.name, l.contact, l.stations, l.city, l.comment, l.page, l.notified_at,
                   l.stage, l.stage_at, l.stage_by, l.refuse_reason, l.refuse_comment, l.note,
                   l.organization_id, o.name AS organization, o.trial_until,
                   (SELECT count(*)::int FROM stations s WHERE s.organization_id = l.organization_id) AS organization_stations
            FROM leads l LEFT JOIN organizations o ON o.id = l.organization_id ORDER BY l.id DESC LIMIT @limit
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

    /// <summary>Letters and digits that cannot be mistaken for each other when read out over the phone.</summary>
    private const string PasswordAlphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string TemporaryPassword() => RandomNumberGenerator.GetString(PasswordAlphabet, 12);

    /// <summary>
    /// "new": an organization plus its owner with a temporary password (shown once, changed at the first sign-in);
    /// "existing": the request is attached to an organization that is already a client. Either way the request is
    /// in work and remembers its organization, so the first station's data moves it to "connected" (LeadNotifier).
    /// </summary>
    private static async Task<IResult> CreateClient(long id, LeadClientBody body, ClaimsPrincipal user, NpgsqlDataSource db, UserService users,
                                                    IOptions<CentralOptions> options)
    {
        await using var c = await db.OpenConnectionAsync();
        var lead = await c.QuerySingleOrDefaultAsync<(string Stage, Guid? Org)>("SELECT stage, organization_id FROM leads WHERE id = @id", new { id });
        if (lead == default) return Results.NotFound();
        if (lead.Org is not null) return Results.Problem(statusCode: 409, title: "По этой заявке клиент уже создан");

        Guid org;
        string orgName;
        string? login = null, password = null;
        if (body.Mode == "existing")
        {
            if (body.OrganizationId is not { } existing) return Results.Problem(statusCode: 400, title: "Выберите организацию");
            var name = await c.ExecuteScalarAsync<string?>("SELECT name FROM organizations WHERE id = @existing", new { existing });
            if (name is null) return Results.Problem(statusCode: 400, title: "Организация не найдена");
            (org, orgName) = (existing, name);
        }
        else if (body.Mode == "new")
        {
            orgName = body.OrganizationName?.Trim() ?? "";
            if (orgName.Length is 0 or > 100) return Results.Problem(statusCode: 400, title: "Название организации: от 1 до 100 символов");
            login = body.OwnerLogin?.Trim() ?? "";
            if (login.Length is < 3 or > 50 || login.Any(char.IsWhiteSpace)) return Results.Problem(statusCode: 400, title: "Логин владельца: от 3 до 50 символов, без пробелов");
            var ownerName = string.IsNullOrWhiteSpace(body.OwnerName) ? login : body.OwnerName.Trim();
            if (await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM users WHERE login_normalized = @n)", new { n = UserService.Normalize(login) }))
                return Results.Problem(statusCode: 409, title: "Такой логин уже занят");
            org = Guid.NewGuid();
            try
            {
                await c.ExecuteAsync("INSERT INTO organizations (id, name, name_normalized) VALUES (@org, @orgName, @norm)",
                    new { org, orgName, norm = orgName.ToLowerInvariant() });
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Results.Problem(statusCode: 409, title: "Организация с таким названием уже есть");
            }
            password = TemporaryPassword();
            Guid owner;
            try { owner = await users.CreateAsync(login, ownerName, Roles.Owner, password, null, org); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                await c.ExecuteAsync("DELETE FROM organizations WHERE id = @org", new { org });
                return Results.Problem(statusCode: 409, title: "Такой логин уже занят");
            }
            await c.ExecuteAsync("UPDATE users SET must_change_password = true WHERE id = @owner", new { owner });
            await Audit.WritePlatformAsync(db, user, "organization_created", "organization:" + org, new { name = orgName, lead = id });
            await Audit.WriteAsync(db, user.UserId(), user.Identity?.Name, "user_created", "user:" + owner, new { login, role = Roles.Owner, temporary_password = true }, org);
        }
        else return Results.Problem(statusCode: 400, title: "mode: new | existing");

        await c.ExecuteAsync("""
            UPDATE leads SET organization_id = @org, client_at = now(), client_created = @createdOrg,
                stage = CASE WHEN stage IN ('new', 'refused') THEN 'work' ELSE stage END,
                stage_at = CASE WHEN stage IN ('new', 'refused') THEN now() ELSE stage_at END,
                stage_by = CASE WHEN stage IN ('new', 'refused') THEN @by ELSE stage_by END,
                refuse_reason = NULL, refuse_comment = NULL
            WHERE id = @id
            """, new { id, org, by = user.Identity?.Name, createdOrg = body.Mode == "new" });
        await Audit.WritePlatformAsync(db, user, "lead_client", "lead:" + id, new { organization = org, mode = body.Mode });
        return Results.Ok(new { organization_id = org, organization = orgName, login, password, service_url = options.Value.PublicOrigin });
    }
}
