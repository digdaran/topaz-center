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
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, UserService.Principal(user));
        await Audit.WriteAsync(db, user.Id, user.Login, "login", null, new { ip = ctx.Connection.RemoteIpAddress?.ToString() });
        return Results.Ok(new { id = user.Id, login = user.Login, name = user.Name, role = user.Role });
    }

    private static async Task<IResult> Me(ClaimsPrincipal user, StationScope scope)
    {
        var stations = await scope.AllowedAsync(user);
        return Results.Ok(new
        {
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
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, UserService.Principal(me)); // keep this session
        await Audit.WriteAsync(db, me.Id, me.Login, "password_changed", "user:" + me.Id, null);
        return Results.NoContent();
    }
}

public static class Audit
{
    public static async Task WriteAsync(NpgsqlDataSource db, Guid? userId, string? login, string action, string? entity, object? details)
    {
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync("INSERT INTO audit_log (user_id, user_login, action, entity, details) VALUES (@userId, @login, @action, @entity, @details::jsonb)",
            new { userId, login, action, entity, details = details is null ? null : JsonSerializer.Serialize(details) });
    }

    public static Task WriteAsync(NpgsqlDataSource db, ClaimsPrincipal user, string action, string? entity, object? details) =>
        WriteAsync(db, user.UserId(), user.Identity?.Name, action, entity, details);
}
