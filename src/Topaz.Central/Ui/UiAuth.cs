using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace Topaz.Central.Ui;

public static class Roles
{
    public const string Owner = "owner";
    public const string Manager = "manager";
    public const string Viewer = "viewer";
    public static readonly string[] All = [Owner, Manager, Viewer];

    /// <summary>Owners and managers see costs and margins; viewers do not.</summary>
    public static bool SeesMoney(ClaimsPrincipal u) => u.IsInRole(Owner) || u.IsInRole(Manager);
}

public static class Policies
{
    public const string Ui = "ui";
    public const string Owner = "ui-owner";
    public const string Manager = "ui-manager"; // owner or manager
}

public sealed class UserRecord
{
    public Guid Id { get; set; }
    public string Login { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public Guid SecurityStamp { get; set; }
    public bool IsActive { get; set; }
    public int FailedLogins { get; set; }
    public DateTime? LockedUntil { get; set; }
}

/// <summary>User storage, password hashing (PBKDF2 via ASP.NET Identity) and login checks.</summary>
public sealed class UserService(NpgsqlDataSource db, IMemoryCache cache)
{
    public const int MinPasswordLength = 10;
    public const int MaxFailedLogins = 5;
    public static readonly TimeSpan LockoutFor = TimeSpan.FromMinutes(15);

    private static readonly PasswordHasher<UserRecord> Hasher = new();

    private const string SelectUser = """
        SELECT id, login, name, role, password_hash AS PasswordHash, security_stamp AS SecurityStamp,
               is_active AS IsActive, failed_logins AS FailedLogins, locked_until AS LockedUntil
        FROM users
        """;

    public static string? ValidatePassword(string? password) =>
        password is null || password.Length < MinPasswordLength ? $"Пароль должен быть не короче {MinPasswordLength} символов" : null;

    public static string HashPassword(string password) => Hasher.HashPassword(new UserRecord(), password);

    public async Task<UserRecord?> FindByIdAsync(Guid id)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.QuerySingleOrDefaultAsync<UserRecord>(SelectUser + " WHERE id = @id", new { id });
    }

    /// <summary>Cached lookup used on every request to re-validate the session cookie.</summary>
    public async Task<UserRecord?> FindCachedAsync(Guid id) =>
        await cache.GetOrCreateAsync("user:" + id, e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(20);
            return FindByIdAsync(id);
        });

    public void Forget(Guid id) => cache.Remove("user:" + id);

    public enum LoginResult { Ok, Invalid, Locked }

    public async Task<(LoginResult Result, UserRecord? User)> CheckLoginAsync(string login, string password)
    {
        await using var c = await db.OpenConnectionAsync();
        var user = await c.QuerySingleOrDefaultAsync<UserRecord>(SelectUser + " WHERE login_normalized = @l", new { l = Normalize(login) });
        if (user is null || !user.IsActive)
        {
            Hasher.VerifyHashedPassword(new UserRecord(), DummyHash, password); // same timing as a real check
            return (LoginResult.Invalid, null);
        }
        if (user.LockedUntil is { } until && until > DateTime.UtcNow) return (LoginResult.Locked, null);

        var verdict = Hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (verdict == PasswordVerificationResult.Failed)
        {
            await c.ExecuteAsync("""
                UPDATE users SET failed_logins = failed_logins + 1,
                    locked_until = CASE WHEN failed_logins + 1 >= @max THEN now() + @lock ELSE locked_until END
                WHERE id = @id
                """, new { id = user.Id, max = MaxFailedLogins, @lock = LockoutFor });
            return (LoginResult.Invalid, null);
        }
        var newHash = verdict == PasswordVerificationResult.SuccessRehashNeeded ? HashPassword(password) : null;
        await c.ExecuteAsync("""
            UPDATE users SET failed_logins = 0, locked_until = NULL, last_login_at = now(),
                password_hash = COALESCE(@newHash, password_hash)
            WHERE id = @id
            """, new { id = user.Id, newHash });
        return (LoginResult.Ok, user);
    }

    public async Task<Guid> CreateAsync(string login, string name, string role, string password, IEnumerable<Guid>? stations = null)
    {
        if (!Roles.All.Contains(role)) throw new ArgumentException("Unknown role " + role);
        if (ValidatePassword(password) is { } error) throw new ArgumentException(error);
        var id = Guid.NewGuid();
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await c.ExecuteAsync("""
            INSERT INTO users (id, login, login_normalized, name, role, password_hash, security_stamp)
            VALUES (@id, @login, @norm, @name, @role, @hash, @stamp)
            """, new { id, login = login.Trim(), norm = Normalize(login), name = name.Trim(), role, hash = HashPassword(password), stamp = Guid.NewGuid() }, tx);
        foreach (var s in stations ?? [])
            await c.ExecuteAsync("INSERT INTO user_stations (user_id, station_id) VALUES (@id, @s)", new { id, s }, tx);
        await tx.CommitAsync();
        return id;
    }

    public static string Normalize(string login) => login.Trim().ToLowerInvariant();

    private static readonly string DummyHash = HashPassword(Guid.NewGuid().ToString());

    public static ClaimsPrincipal Principal(UserRecord u) => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()),
        new Claim(ClaimTypes.Name, u.Login),
        new Claim("display_name", u.Name),
        new Claim(ClaimTypes.Role, u.Role),
        new Claim("stamp", u.SecurityStamp.ToString()),
    ], CookieAuthenticationDefaults.AuthenticationScheme));
}

public static class UiUserExtensions
{
    public static Guid UserId(this ClaimsPrincipal u) => Guid.Parse(u.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public static string Role(this ClaimsPrincipal u) => u.FindFirstValue(ClaimTypes.Role) ?? Roles.Viewer;
}

/// <summary>Cookie session setup: 401/403 instead of redirects; session re-validated against the DB.</summary>
public static class UiAuthSetup
{
    public const string CookieName = "topaz_ui";
    public const string CsrfHeader = "X-Topaz-Ui";

    public static void AddUiAuth(this IServiceCollection services)
    {
        services.AddSingleton<UserService>();
        services.AddSingleton<StationScope>();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = CookieName;
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // HTTPS behind Caddy (X-Forwarded-Proto)
                o.ExpireTimeSpan = TimeSpan.FromHours(12);
                o.SlidingExpiration = true;
                o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
                o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
                o.Events.OnValidatePrincipal = async ctx =>
                {
                    var users = ctx.HttpContext.RequestServices.GetRequiredService<UserService>();
                    var id = Guid.TryParse(ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var g) ? g : Guid.Empty;
                    var user = id == Guid.Empty ? null : await users.FindCachedAsync(id);
                    if (user is null || !user.IsActive || user.SecurityStamp.ToString() != ctx.Principal!.FindFirstValue("stamp"))
                    {
                        ctx.RejectPrincipal();
                        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    }
                };
            });
        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Ui, p => p.RequireAuthenticatedUser())
            .AddPolicy(Policies.Owner, p => p.RequireRole(Roles.Owner))
            .AddPolicy(Policies.Manager, p => p.RequireRole(Roles.Owner, Roles.Manager));
    }

    /// <summary>CSRF guard for state-changing UI calls: a custom header cannot be sent cross-site without CORS.</summary>
    public sealed class CsrfFilter : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var req = context.HttpContext.Request;
            if (!HttpMethods.IsGet(req.Method) && !HttpMethods.IsHead(req.Method) && req.Headers[CsrfHeader] != "1")
                return Results.Problem(statusCode: 400, title: $"Header {CsrfHeader}: 1 is required");
            return await next(context);
        }
    }
}

/// <summary>Which stations the current user may see. Every UI query is restricted through this.</summary>
public sealed class StationScope(NpgsqlDataSource db, IMemoryCache cache)
{
    public async Task<Guid[]> AllowedAsync(ClaimsPrincipal user)
    {
        var uid = user.UserId();
        var owner = user.IsInRole(Roles.Owner);
        return (await cache.GetOrCreateAsync($"scope:{uid}:{owner}", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(20);
            await using var c = await db.OpenConnectionAsync();
            var ids = owner
                ? await c.QueryAsync<Guid>("SELECT id FROM stations WHERE status <> 'revoked'")
                : await c.QueryAsync<Guid>("SELECT us.station_id FROM user_stations us JOIN stations s ON s.id = us.station_id WHERE us.user_id = @uid AND s.status <> 'revoked'", new { uid });
            return ids.ToArray();
        }))!;
    }

    public async Task<bool> CanSeeAsync(ClaimsPrincipal user, Guid station) => (await AllowedAsync(user)).Contains(station);

    public void Forget(Guid userId)
    {
        cache.Remove($"scope:{userId}:True");
        cache.Remove($"scope:{userId}:False");
    }
}
