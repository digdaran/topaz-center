using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Npgsql;

namespace Topaz.Central.Ui;

public sealed record LoginRequest(string Login, string Password);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public static class AuthEndpoints
{
    public static void MapUiAuth(this RouteGroupBuilder ui)
    {
        ui.MapPost("/auth/login", Login).AllowAnonymous().RequireRateLimiting("login");
        ui.MapPost("/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });
        ui.MapGet("/auth/me", Me);
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

    private static async Task<IResult> Login(LoginRequest body, HttpContext ctx, UserService users, NpgsqlDataSource db)
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
        var selected = user.Role == Roles.Superadmin ? await DefaultOrganizationAsync(db) : null;
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, UserService.Principal(user, selected));
        await Audit.WriteAsync(db, user.Id, user.Login, "login", null, new { ip = ctx.Connection.RemoteIpAddress?.ToString() }, user.OrganizationId);
        return Results.Ok(new { id = user.Id, login = user.Login, name = user.Name, role = user.Role });
    }

    private static async Task<IResult> Me(ClaimsPrincipal user, StationScope scope, NpgsqlDataSource db)
    {
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
