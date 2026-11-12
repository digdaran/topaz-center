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
    /// <summary>Everything an owner can do, plus connecting stations, agent updates, users and the audit log.</summary>
    public const string Superadmin = "superadmin";
    public const string Owner = "owner";
    public const string Manager = "manager";
    public const string Viewer = "viewer";
    public static readonly string[] All = [Superadmin, Owner, Manager, Viewer];

    /// <summary>Owners and managers see costs and margins; viewers do not.</summary>
    public static bool SeesMoney(ClaimsPrincipal u) => u.IsInRole(Owner) || u.IsInRole(Manager);
}

public static class Policies
{
    public const string Ui = "ui";
    public const string Super = "ui-super";
    public const string Owner = "ui-owner"; // owner or superadmin
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
    /// <summary>Null for a superadmin.</summary>
    public Guid? OrganizationId { get; set; }
    public bool OrganizationActive { get; set; } = true;
    /// <summary>Encrypted TOTP secret; null = two-factor login off.</summary>
    public string? TotpSecret { get; set; }
    public string? TotpPending { get; set; }
    public long? TotpLastStep { get; set; }
    public string[] RecoveryCodes { get; set; } = [];
    public bool TotpEnabled => TotpSecret is not null;
}

/// <summary>User storage, password hashing (PBKDF2 via ASP.NET Identity) and login checks.</summary>
public sealed class UserService(NpgsqlDataSource db, IMemoryCache cache)
{
    public const int MinPasswordLength = 10;
    public const int MaxFailedLogins = 5;
    public static readonly TimeSpan LockoutFor = TimeSpan.FromMinutes(15);

    private static readonly PasswordHasher<UserRecord> Hasher = new();

    private const string SelectUser = """
        SELECT u.id, u.login, u.name, u.role, u.password_hash AS PasswordHash, u.security_stamp AS SecurityStamp,
               u.is_active AS IsActive, u.failed_logins AS FailedLogins, u.locked_until AS LockedUntil,
               u.organization_id AS OrganizationId, COALESCE(o.is_active, true) AS OrganizationActive,
               u.totp_secret AS TotpSecret, u.totp_pending AS TotpPending, u.totp_last_step AS TotpLastStep,
               u.recovery_codes AS RecoveryCodes
        FROM users u LEFT JOIN organizations o ON o.id = u.organization_id
        """;

    public static string? ValidatePassword(string? password) =>
        password is null || password.Length < MinPasswordLength ? $"Пароль должен быть не короче {MinPasswordLength} символов" : null;

    public static string HashPassword(string password) => Hasher.HashPassword(new UserRecord(), password);

    public async Task<UserRecord?> FindByIdAsync(Guid id)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.QuerySingleOrDefaultAsync<UserRecord>(SelectUser + " WHERE u.id = @id", new { id });
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
        var user = await c.QuerySingleOrDefaultAsync<UserRecord>(SelectUser + " WHERE u.login_normalized = @l", new { l = Normalize(login) });
        if (user is null || !user.IsActive || !user.OrganizationActive)
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
        // With two-factor login the failure counter is reset only after the code: otherwise the password step
        // would reset it between guesses of the code.
        await c.ExecuteAsync("""
            UPDATE users SET password_hash = COALESCE(@newHash, password_hash),
                failed_logins = CASE WHEN @twoFactor THEN failed_logins ELSE 0 END,
                locked_until = CASE WHEN @twoFactor THEN locked_until ELSE NULL END,
                last_login_at = CASE WHEN @twoFactor THEN last_login_at ELSE now() END
            WHERE id = @id
            """, new { id = user.Id, newHash, twoFactor = user.TotpEnabled });
        return (LoginResult.Ok, user);
    }

    public enum CodeResult { Ok, Invalid, Locked }

    /// <summary>Turns two-factor login off (own choice, an owner/superadmin reset, CLI); ends all sessions.</summary>
    public const string ResetTotpSql = """
        UPDATE users SET totp_secret = NULL, totp_pending = NULL, totp_last_step = NULL, totp_enabled_at = NULL,
            recovery_codes = '{}', security_stamp = @stamp
        WHERE id = @id
        """;

    /// <summary>A lost phone (owner/superadmin reset, CLI): the app's codes and all passkeys stop working; sessions end.</summary>
    public const string ResetSecondFactorsSql = """
        WITH gone AS (DELETE FROM user_passkeys WHERE user_id = @id)
        UPDATE users SET totp_secret = NULL, totp_pending = NULL, totp_last_step = NULL, totp_enabled_at = NULL,
            recovery_codes = '{}', security_stamp = @stamp
        WHERE id = @id
        """;

    /// <summary>
    /// Second login step: a code from the app (each 30-second code once) or a recovery code (each once).
    /// A wrong code counts as a failed login, so the lockout covers guessing.
    /// </summary>
    public async Task<CodeResult> CheckCodeAsync(UserRecord user, string? code, TotpProtector protector)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var row = await c.QuerySingleOrDefaultAsync<UserRecord>(SelectUser + " WHERE u.id = @id FOR UPDATE OF u", new { id = user.Id }, tx);
        if (row?.TotpSecret is null) return CodeResult.Invalid;
        if (row.LockedUntil is { } until && until > DateTime.UtcNow) return CodeResult.Locked;

        var step = Totp.Verify(protector.Unprotect(row.TotpSecret), code, DateTimeOffset.UtcNow, row.TotpLastStep);
        var recoveryHash = step is null && (code ?? "").Contains('-') ? Totp.HashRecoveryCode(code!) : null;
        var recovery = recoveryHash is not null && row.RecoveryCodes.Contains(recoveryHash);
        if (step is null && !recovery)
        {
            await c.ExecuteAsync("""
                UPDATE users SET failed_logins = failed_logins + 1,
                    locked_until = CASE WHEN failed_logins + 1 >= @max THEN now() + @lock ELSE locked_until END
                WHERE id = @id
                """, new { id = row.Id, max = MaxFailedLogins, @lock = LockoutFor }, tx);
            await tx.CommitAsync();
            return CodeResult.Invalid;
        }
        await c.ExecuteAsync("""
            UPDATE users SET failed_logins = 0, locked_until = NULL, last_login_at = now(),
                totp_last_step = COALESCE(@step, totp_last_step),
                recovery_codes = CASE WHEN @recoveryHash IS NULL THEN recovery_codes ELSE array_remove(recovery_codes, @recoveryHash) END
            WHERE id = @id
            """, new { id = row.Id, step, recoveryHash = recovery ? recoveryHash : null }, tx);
        await tx.CommitAsync();
        Forget(row.Id);
        return CodeResult.Ok;
    }

    public async Task<Guid> CreateAsync(string login, string name, string role, string password, IEnumerable<Guid>? stations = null, Guid? organizationId = null)
    {
        if (!Roles.All.Contains(role)) throw new ArgumentException("Unknown role " + role);
        if (role != Roles.Superadmin && organizationId is null)
        {
            // Without an explicit organization (CLI, tests): the only/first one.
            await using var oc = await db.OpenConnectionAsync();
            organizationId = await oc.ExecuteScalarAsync<Guid?>("SELECT id FROM organizations ORDER BY created_at, name LIMIT 1")
                             ?? throw new ArgumentException("Create an organization first");
        }
        if (role == Roles.Superadmin) organizationId = null;
        if (ValidatePassword(password) is { } error) throw new ArgumentException(error);
        var id = Guid.NewGuid();
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await c.ExecuteAsync("""
            INSERT INTO users (id, login, login_normalized, name, role, password_hash, security_stamp, organization_id)
            VALUES (@id, @login, @norm, @name, @role, @hash, @stamp, @organizationId)
            """, new { id, login = login.Trim(), norm = Normalize(login), name = name.Trim(), role, hash = HashPassword(password), stamp = Guid.NewGuid(), organizationId }, tx);
        foreach (var s in stations ?? [])
            await c.ExecuteAsync("INSERT INTO user_stations (user_id, station_id) VALUES (@id, @s)", new { id, s }, tx);
        await tx.CommitAsync();
        return id;
    }

    public static string Normalize(string login) => login.Trim().ToLowerInvariant();

    private static readonly string DummyHash = HashPassword(Guid.NewGuid().ToString());

    /// <summary>Session identity; a superadmin carries the organization selected in the header.</summary>
    public static ClaimsPrincipal Principal(UserRecord u, Guid? selectedOrganization = null) => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()),
        new Claim(ClaimTypes.Name, u.Login),
        new Claim("display_name", u.Name),
        new Claim(ClaimTypes.Role, u.Role),
        // A superadmin is also an owner, so every owner check covers it.
        .. (u.Role == Roles.Superadmin ? [new Claim(ClaimTypes.Role, Roles.Owner)] : Array.Empty<Claim>()),
        new Claim("stamp", u.SecurityStamp.ToString()),
        .. ((u.OrganizationId ?? selectedOrganization) is { } org ? [new Claim(OrganizationClaims.Claim, org.ToString())] : Array.Empty<Claim>()),
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
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(7);

    public static void AddUiAuth(this IServiceCollection services, bool secureCookies)
    {
        services.AddSingleton<UserService>();
        services.AddSingleton<TotpProtector>();
        services.AddSingleton<PasskeyChallenges>();
        services.AddSingleton<StationScope>();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = CookieName;
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict;
                // Never sent over plain http; tests and a local stack without TLS set Topaz__SecureCookies=false.
                o.Cookie.SecurePolicy = secureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
                // A week since the last visit; the cookie survives closing the browser (phones drop tabs and
                // session cookies often). Logout, a new password, blocking or a reset end it at once (security stamp).
                o.ExpireTimeSpan = SessionLifetime;
                o.SlidingExpiration = true;
                o.Events.OnSigningIn = ctx => { ctx.Properties.IsPersistent = true; return Task.CompletedTask; };
                o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
                o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
                o.Events.OnValidatePrincipal = async ctx =>
                {
                    var users = ctx.HttpContext.RequestServices.GetRequiredService<UserService>();
                    var id = Guid.TryParse(ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var g) ? g : Guid.Empty;
                    var user = id == Guid.Empty ? null : await users.FindCachedAsync(id);
                    if (user is null || !user.IsActive || !user.OrganizationActive || user.SecurityStamp.ToString() != ctx.Principal!.FindFirstValue("stamp"))
                    {
                        ctx.RejectPrincipal();
                        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    }
                };
            });
        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Ui, p => p.RequireAuthenticatedUser())
            .AddPolicy(Policies.Super, p => p.RequireRole(Roles.Superadmin))
            .AddPolicy(Policies.Owner, p => p.RequireRole(Roles.Owner))
            .AddPolicy(Policies.Manager, p => p.RequireRole(Roles.Owner, Roles.Manager));
    }

    /// <summary>
    /// Two-factor login is mandatory for superadmins (Topaz__RequireTotpForSuperadmin): until it is on, only
    /// /auth/* answers (me, logout, setting it up). Checked against the user row, so older sessions are covered too.
    /// </summary>
    public sealed class TotpRequiredFilter(UserService users, Microsoft.Extensions.Options.IOptions<CentralOptions> options) : IEndpointFilter
    {
        public const string SetupRequired = "Включите двухфакторную защиту входа: Настройки → Вход и защита";

        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var http = context.HttpContext;
            if (options.Value.RequireTotpForSuperadmin && http.User.IsInRole(Roles.Superadmin)
                && !http.Request.Path.StartsWithSegments(Topaz.Contracts.Protocol.ApiPrefix + "/ui/auth")
                && Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
                && await users.FindCachedAsync(id) is { TotpEnabled: false })
                return Results.Problem(statusCode: 403, title: SetupRequired, type: "totp_setup_required");
            return await next(context);
        }
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

/// <summary>
/// Which stations the current user may see — always within one organization (the user's own; for a superadmin the one
/// selected in the header). Every UI query is restricted through this; the UI pool's row level security repeats it.
/// </summary>
public sealed class StationScope(NpgsqlDataSource db, IMemoryCache cache)
{
    public async Task<Guid[]> AllowedAsync(ClaimsPrincipal user)
    {
        if (user.OrganizationId() is not { } org) return [];
        var uid = user.UserId();
        if (user.IsInRole(Roles.Owner))
        {
            // Not cached: a newly connected station must show up for the owner immediately (one tiny query).
            await using var oc = await db.OpenConnectionAsync();
            return (await oc.QueryAsync<Guid>("SELECT id FROM stations WHERE organization_id = @org", new { org })).ToArray(); // blocked ones too: history stays visible
        }
        return (await cache.GetOrCreateAsync($"scope:{uid}:{org}", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(20);
            await using var c = await db.OpenConnectionAsync();
            return (await c.QueryAsync<Guid>("""
                SELECT us.station_id FROM user_stations us JOIN stations s ON s.id = us.station_id
                WHERE us.user_id = @uid AND s.organization_id = @org AND s.status <> 'revoked'
                """, new { uid, org })).ToArray();
        }))!;
    }

    public async Task<bool> CanSeeAsync(ClaimsPrincipal user, Guid station) => (await AllowedAsync(user)).Contains(station);

    public void Forget(Guid userId)
    {
        // Keys include the organization; a moved user also gets a new security stamp (new session).
        if (cache is MemoryCache mc) mc.Compact(1.0);
    }
}
