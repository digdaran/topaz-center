using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using Npgsql;
using Topaz.Contracts;

namespace Topaz.Central.Ui;

public sealed record LoginRequest(string Login, string Password);
public sealed record CodeRequest(string Code);
public sealed record TotpSetupRequest(string Password);
public sealed record TotpDisableRequest(string Password, string Code);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public static class AuthEndpoints
{
    public static void MapUiAuth(this RouteGroupBuilder ui)
    {
        ui.MapPost("/auth/login", Login).AllowAnonymous().RequireRateLimiting("login");
        ui.MapPost("/auth/login/code", LoginCode).AllowAnonymous().RequireRateLimiting("login");
        ui.MapPost("/auth/totp/setup", TotpSetup).RequireRateLimiting("login");
        ui.MapPost("/auth/totp/enable", TotpEnable).RequireRateLimiting("login");
        ui.MapPost("/auth/totp/disable", TotpDisable).RequireRateLimiting("login");
        ui.MapPost("/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });
        ui.MapGet("/auth/me", Me);
        ui.MapUiPasskeys();
        ui.MapPost("/auth/password", ChangePassword);
        ui.MapPost("/session/organization", SwitchOrganization).RequireAuthorization(Policies.Super);
    }

    public sealed record SwitchBody(Guid Id);

    /// <summary>The superadmin looks at one organization at a time; every switch is in the audit log.</summary>
    private static async Task<IResult> SwitchOrganization(SwitchBody body, HttpContext ctx, UserService users, NpgsqlDataSource db)
    {
        await using var c = await db.OpenConnectionAsync();
        var name = await c.ExecuteScalarAsync<string?>("SELECT name FROM organizations WHERE id = @id", new { id = body.Id });
        if (name is null) return Results.NotFound();
        var me = await users.FindByIdAsync(ctx.User.UserId());
        if (me is null) return Results.Unauthorized();
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, UserService.Principal(me, body.Id));
        await Audit.WriteAsync(db, me.Id, me.Login, "organization_switched", "organization:" + body.Id, new { name }, body.Id);
        return Results.NoContent();
    }

    /// <summary>A superadmin starts in the first active organization.</summary>
    private static async Task<Guid?> DefaultOrganizationAsync(NpgsqlDataSource db)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<Guid?>("SELECT id FROM organizations WHERE is_active ORDER BY created_at, name LIMIT 1");
    }

    /// <summary>Between the password and the code: who passed the password step (encrypted, 5 minutes).</summary>
    public const string TicketCookie = "topaz_ui_2fa";
    private const string Issuer = "Topaz Center";

    private static CookieOptions TicketCookieOptions(HttpContext ctx, IOptions<CentralOptions> options) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = options.Value.SecureCookies || ctx.Request.IsHttps,
        Path = Protocol.ApiPrefix + "/ui/auth",
        MaxAge = TotpProtector.TicketLifetime,
    };

    internal static async Task<IResult> SignInAsync(HttpContext ctx, UserRecord user, NpgsqlDataSource db, string action, string? method = null)
    {
        var selected = user.Role == Roles.Superadmin ? await DefaultOrganizationAsync(db) : null;
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, UserService.Principal(user, selected));
        object details = method is null ? new { ip = ctx.Connection.RemoteIpAddress?.ToString() } : new { ip = ctx.Connection.RemoteIpAddress?.ToString(), method };
        await Audit.WriteAsync(db, user.Id, user.Login, action, null, details, user.OrganizationId);
        return Results.Ok(new { id = user.Id, login = user.Login, name = user.Name, role = user.Role, need_code = false });
    }

    private static async Task<IResult> Login(LoginRequest body, HttpContext ctx, UserService users, NpgsqlDataSource db,
        TotpProtector protector, IOptions<CentralOptions> options)
    {
        if (string.IsNullOrWhiteSpace(body.Login) || string.IsNullOrEmpty(body.Password))
            return Results.Problem(statusCode: 400, title: "Введите логин и пароль");
        var (result, user) = await users.CheckLoginAsync(body.Login, body.Password);
        if (result == UserService.LoginResult.Locked)
            return Results.Problem(statusCode: 423, title: "Слишком много неудачных попыток. Попробуйте через 15 минут.");
        if (user is null)
        {
            await Audit.WriteAsync(db, null, body.Login, "login_failed", null, new { ip = ctx.Connection.RemoteIpAddress?.ToString() });
            return Results.Problem(statusCode: 401, title: "Неверный логин или пароль");
        }
        if (!user.TotpEnabled) return await SignInAsync(ctx, user, db, "login");
        // Two-factor login: no session yet, only a short-lived ticket for the code step.
        ctx.Response.Cookies.Append(TicketCookie, protector.Ticket(user.Id, user.SecurityStamp), TicketCookieOptions(ctx, options));
        return Results.Ok(new { need_code = true });
    }

    private static async Task<IResult> LoginCode(CodeRequest body, HttpContext ctx, UserService users, NpgsqlDataSource db,
        TotpProtector protector, IOptions<CentralOptions> options)
    {
        var ticket = protector.ReadTicket(ctx.Request.Cookies[TicketCookie]);
        var user = ticket is { } t ? await users.FindByIdAsync(t.UserId) : null;
        if (user is null || user.SecurityStamp != ticket!.Value.Stamp || !user.IsActive || !user.OrganizationActive)
            return Results.Problem(statusCode: 401, title: "Время на ввод кода истекло, войдите заново");
        var result = await users.CheckCodeAsync(user, body.Code, protector);
        if (result == UserService.CodeResult.Locked)
            return Results.Problem(statusCode: 423, title: "Слишком много неудачных попыток. Попробуйте через 15 минут.");
        if (result != UserService.CodeResult.Ok)
        {
            await Audit.WriteAsync(db, user.Id, user.Login, "login_code_failed", null, new { ip = ctx.Connection.RemoteIpAddress?.ToString() }, user.OrganizationId);
            return Results.Problem(statusCode: 401, title: "Неверный код");
        }
        ctx.Response.Cookies.Delete(TicketCookie, TicketCookieOptions(ctx, options));
        return await SignInAsync(ctx, user, db, "login");
    }

    /// <summary>Step 1 of turning two-factor login on: a new secret for the app (shown as a QR code).</summary>
    private static async Task<IResult> TotpSetup(TotpSetupRequest body, ClaimsPrincipal principal, UserService users, NpgsqlDataSource db, TotpProtector protector)
    {
        var me = await users.FindByIdAsync(principal.UserId());
        if (me is null) return Results.Unauthorized();
        if (me.TotpEnabled) return Results.Problem(statusCode: 409, title: "Двухфакторная защита уже включена");
        var (check, _) = await users.CheckLoginAsync(me.Login, body.Password ?? "");
        if (check != UserService.LoginResult.Ok) return Results.Problem(statusCode: 400, title: "Пароль неверен");
        var secret = Totp.NewSecret();
        await using (var c = await db.OpenConnectionAsync())
            await c.ExecuteAsync("UPDATE users SET totp_pending = @p WHERE id = @id", new { id = me.Id, p = protector.Protect(secret) });
        users.Forget(me.Id);
        return Results.Ok(new { secret = Totp.Base32(secret), uri = Totp.Uri(secret, Issuer, me.Login) });
    }

    /// <summary>Step 2: the first code from the app turns it on; the recovery codes are returned once.</summary>
    private static async Task<IResult> TotpEnable(CodeRequest body, ClaimsPrincipal principal, HttpContext ctx, UserService users, NpgsqlDataSource db, TotpProtector protector)
    {
        var me = await users.FindByIdAsync(principal.UserId());
        if (me is null) return Results.Unauthorized();
        if (me.TotpPending is null) return Results.Problem(statusCode: 400, title: "Сначала получите QR-код");
        var step = Totp.Verify(protector.Unprotect(me.TotpPending), body.Code, DateTimeOffset.UtcNow, null);
        if (step is null) return Results.Problem(statusCode: 400, title: "Код не подошёл. Проверьте время на телефоне и введите свежий код.");
        var codes = Totp.NewRecoveryCodes();
        var stamp = Guid.NewGuid(); // other sessions (password only) end
        await using (var c = await db.OpenConnectionAsync())
            await c.ExecuteAsync("""
                UPDATE users SET totp_secret = totp_pending, totp_pending = NULL, totp_last_step = @step, totp_enabled_at = now(),
                    recovery_codes = @hashes, security_stamp = @stamp
                WHERE id = @id
                """, new { id = me.Id, step, hashes = codes.Select(Totp.HashRecoveryCode).ToArray(), stamp });
        users.Forget(me.Id);
        me.SecurityStamp = stamp;
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, UserService.Principal(me, principal.OrganizationId()));
        await Audit.WriteAsync(db, me.Id, me.Login, "totp_enabled", "user:" + me.Id, null, me.OrganizationId);
        return Results.Ok(new { recovery_codes = codes });
    }

    private static async Task<IResult> TotpDisable(TotpDisableRequest body, ClaimsPrincipal principal, HttpContext ctx, UserService users,
        NpgsqlDataSource db, TotpProtector protector, IOptions<CentralOptions> options)
    {
        var me = await users.FindByIdAsync(principal.UserId());
        if (me is null) return Results.Unauthorized();
        if (!me.TotpEnabled) return Results.NoContent();
        if (options.Value.RequireTotpForSuperadmin && me.Role == Roles.Superadmin)
            return Results.Problem(statusCode: 400, title: "Для суперадминистратора двухфакторная защита обязательна");
        var (check, _) = await users.CheckLoginAsync(me.Login, body.Password ?? "");
        if (check != UserService.LoginResult.Ok) return Results.Problem(statusCode: 400, title: "Пароль неверен");
        if (await users.CheckCodeAsync(me, body.Code, protector) != UserService.CodeResult.Ok)
            return Results.Problem(statusCode: 400, title: "Неверный код");
        var stamp = Guid.NewGuid();
        await using (var c = await db.OpenConnectionAsync())
            await c.ExecuteAsync(UserService.ResetTotpSql, new { id = me.Id, stamp });
        users.Forget(me.Id);
        me.SecurityStamp = stamp;
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, UserService.Principal(me, principal.OrganizationId()));
        await Audit.WriteAsync(db, me.Id, me.Login, "totp_disabled", "user:" + me.Id, null, me.OrganizationId);
        return Results.NoContent();
    }

    private static async Task<IResult> Me(ClaimsPrincipal user, StationScope scope, NpgsqlDataSource db, UserService users, IOptions<CentralOptions> options)
    {
        var record = await users.FindCachedAsync(user.UserId());
        var totpEnabled = record?.TotpEnabled ?? false;
        var stations = await scope.AllowedAsync(user);
        await using var c = await db.OpenConnectionAsync();
        var org = user.OrganizationId();
        var orgName = org is null ? null : await c.ExecuteScalarAsync<string?>("SELECT name FROM organizations WHERE id = @org", new { org });
        var organizations = user.IsInRole(Roles.Superadmin)
            ? (await c.QueryAsync("SELECT id, name, is_active FROM organizations ORDER BY name")).ToList()
            : null;
        return Results.Ok(new
        {
            organization_id = org,
            organization = orgName,
            organizations,
            id = user.UserId(),
            login = user.Identity!.Name,
            name = user.FindFirstValue("display_name"),
            role = user.Role(),
            sees_money = Roles.SeesMoney(user),
            station_count = stations.Length,
            totp_enabled = totpEnabled,
            recovery_codes_left = record?.RecoveryCodes.Length ?? 0,
            totp_mandatory = options.Value.RequireTotpForSuperadmin && user.IsInRole(Roles.Superadmin),
            totp_setup_required = options.Value.RequireTotpForSuperadmin && user.IsInRole(Roles.Superadmin) && !totpEnabled,
        });
    }

    private static async Task<IResult> ChangePassword(ChangePasswordRequest body, ClaimsPrincipal principal, HttpContext ctx, UserService users, NpgsqlDataSource db)
    {
        var me = await users.FindByIdAsync(principal.UserId());
        if (me is null) return Results.Unauthorized();
        var (result, _) = await users.CheckLoginAsync(me.Login, body.CurrentPassword ?? "");
        if (result != UserService.LoginResult.Ok) return Results.Problem(statusCode: 400, title: "Текущий пароль неверен");
        if (UserService.ValidatePassword(body.NewPassword) is { } error) return Results.Problem(statusCode: 400, title: error);
        var stamp = Guid.NewGuid();
        await using (var c = await db.OpenConnectionAsync())
            await c.ExecuteAsync("UPDATE users SET password_hash = @h, security_stamp = @stamp WHERE id = @id",
                new { id = me.Id, h = UserService.HashPassword(body.NewPassword!), stamp });
        users.Forget(me.Id);
        me.SecurityStamp = stamp;
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, UserService.Principal(me, principal.OrganizationId())); // keep this session
        await Audit.WriteAsync(db, me.Id, me.Login, "password_changed", "user:" + me.Id, null);
        return Results.NoContent();
    }
}

/// <summary>Audit log; each entry belongs to the organization it concerns (null: platform-wide action).</summary>
public static class Audit
{
    public static async Task WriteAsync(NpgsqlDataSource db, Guid? userId, string? login, string action, string? entity, object? details, Guid? organizationId = null)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync("""
            INSERT INTO audit_log (user_id, user_login, action, entity, details, organization_id)
            VALUES (@userId, @login, @action, @entity, @details::jsonb, @organizationId)
            """, new { userId, login, action, entity, details = details is null ? null : JsonSerializer.Serialize(details), organizationId });
    }

    /// <summary>Organization: the one the user works in (a superadmin: the selected one).</summary>
    public static Task WriteAsync(NpgsqlDataSource db, ClaimsPrincipal user, string action, string? entity, object? details) =>
        WriteAsync(db, user.UserId(), user.Identity?.Name, action, entity, details, user.OrganizationId());

    public static Task WriteAsync(UiDb db, ClaimsPrincipal user, string action, string? entity, object? details) =>
        WriteAsync(db.System, user, action, entity, details);

    /// <summary>Platform-wide actions of a superadmin (organizations, connections, agent updates).</summary>
    public static Task WritePlatformAsync(NpgsqlDataSource db, ClaimsPrincipal user, string action, string? entity, object? details) =>
        WriteAsync(db, user.UserId(), user.Identity?.Name, action, entity, details, null);
}
