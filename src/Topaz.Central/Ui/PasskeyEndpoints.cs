using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Npgsql;
using Topaz.Contracts;

namespace Topaz.Central.Ui;

public sealed record PasskeyOptionsRequest(string? Password);
// WebAuthn's own field names (the browser's PublicKeyCredential JSON), not the API's snake_case.
public sealed record PasskeyResponse(
    [property: JsonPropertyName("clientDataJSON")] string? ClientDataJSON,
    [property: JsonPropertyName("attestationObject")] string? AttestationObject,
    [property: JsonPropertyName("authenticatorData")] string? AuthenticatorData,
    [property: JsonPropertyName("signature")] string? Signature,
    [property: JsonPropertyName("userHandle")] string? UserHandle);
public sealed record PasskeyCredential(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("rawId")] string? RawId,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("response")] PasskeyResponse? Response);
public sealed record PasskeyRegisterRequest(PasskeyCredential? Credential, string? Name);

/// <summary>
/// The challenge of one passkey ceremony, kept in a short-lived encrypted cookie (who, for what, which challenge)
/// and spent once: a captured response cannot be replayed.
/// </summary>
public sealed class PasskeyChallenges(IDataProtectionProvider provider, IMemoryCache cache)
{
    public const string Cookie = "topaz_ui_passkey";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly ITimeLimitedDataProtector _protector =
        provider.CreateProtector("Topaz.Central.Passkey.Challenge.v1").ToTimeLimitedDataProtector();
    private readonly object _gate = new();

    public string Issue(string purpose, Guid userId, Guid stamp, string challenge) =>
        _protector.Protect($"{purpose}|{userId:N}|{stamp:N}|{challenge}", Lifetime);

    /// <summary>The challenge for this purpose, once; null if missing, expired, forged or already used.</summary>
    public (Guid UserId, Guid Stamp, string Challenge)? Take(string? cookie, string purpose)
    {
        if (string.IsNullOrEmpty(cookie)) return null;
        string[] parts;
        try { parts = _protector.Unprotect(cookie, out _).Split('|'); }
        catch (CryptographicException) { return null; }
        if (parts.Length != 4 || parts[0] != purpose || !Guid.TryParse(parts[1], out var user) || !Guid.TryParse(parts[2], out var stamp)) return null;
        var used = false;
        lock (_gate)
        {
            if (cache.TryGetValue("passkey-used:" + parts[3], out _)) used = true;
            else cache.Set("passkey-used:" + parts[3], true, Lifetime);
        }
        return used ? null : (user, stamp, parts[3]);
    }
}

/// <summary>
/// Passkeys: login without a password by fingerprint, face or device PIN (phones and computers). A passkey is
/// two-factor by itself (the device + its biometrics); password + code from the app stay as the fallback.
/// </summary>
public static class PasskeyEndpoints
{
    private const string RpName = "Topaz Center";
    private const int TimeoutMs = 120_000;

    public static void MapUiPasskeys(this RouteGroupBuilder ui)
    {
        ui.MapPost("/auth/passkey/options", LoginOptions).AllowAnonymous().RequireRateLimiting("login");
        ui.MapPost("/auth/passkey/login", Login).AllowAnonymous().RequireRateLimiting("login");
        ui.MapGet("/auth/passkeys", List);
        ui.MapPost("/auth/passkeys/options", RegisterOptions).RequireRateLimiting("login");
        ui.MapPost("/auth/passkeys", Register).RequireRateLimiting("login");
        ui.MapDelete("/auth/passkeys/{id}", Delete);
    }

    /// <summary>
    /// Origin and relying party id: the public address (Topaz__PublicOrigin) or, by default, the address the browser
    /// used (behind Caddy the scheme comes from X-Forwarded-Proto). A passkey belongs to the domain it was made on.
    /// </summary>
    internal static (string Origin, string RpId) Party(HttpContext ctx, CentralOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.PublicOrigin) && Uri.TryCreate(options.PublicOrigin, UriKind.Absolute, out var u))
            return (u.GetLeftPart(UriPartial.Authority), u.Host);
        return ($"{ctx.Request.Scheme}://{ctx.Request.Host}", ctx.Request.Host.Host);
    }

    private static CookieOptions ChallengeCookie(HttpContext ctx, CentralOptions options) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = options.SecureCookies || ctx.Request.IsHttps,
        Path = Protocol.ApiPrefix + "/ui/auth",
        MaxAge = PasskeyChallenges.Lifetime,
    };

    /// <summary>Options exactly as WebAuthn names them (camelCase): a JsonNode is written as is.</summary>
    private static JsonNode WebAuthnJson(object options) => JsonSerializer.SerializeToNode(options)!;

    private static IResult Refused(string title = "Ключ доступа не подошёл. Войдите по паролю.") => Results.Problem(statusCode: 401, title: title);

    // ---- login -------------------------------------------------------------------------------------------------

    /// <summary>Step 1: a challenge; the device offers the passkeys it has for this site (no login typed).</summary>
    private static IResult LoginOptions(HttpContext ctx, PasskeyChallenges challenges, IOptions<CentralOptions> options)
    {
        var (_, rpId) = Party(ctx, options.Value);
        var challenge = WebAuthn.NewChallenge();
        ctx.Response.Cookies.Append(PasskeyChallenges.Cookie, challenges.Issue("login", Guid.Empty, Guid.Empty, challenge), ChallengeCookie(ctx, options.Value));
        return Results.Ok(WebAuthnJson(new { challenge, rpId, timeout = TimeoutMs, userVerification = "required", allowCredentials = Array.Empty<object>() }));
    }

    private sealed record StoredKey(Guid UserId, byte[] PublicKey, long SignCount);

    /// <summary>Step 2: the signed challenge → a session, like password + code.</summary>
    private static async Task<IResult> Login(PasskeyCredential body, HttpContext ctx, PasskeyChallenges challenges, UserService users,
        NpgsqlDataSource db, IOptions<CentralOptions> options, ILoggerFactory logs)
    {
        var taken = challenges.Take(ctx.Request.Cookies[PasskeyChallenges.Cookie], "login");
        ctx.Response.Cookies.Delete(PasskeyChallenges.Cookie, ChallengeCookie(ctx, options.Value));
        if (taken is null) return Refused("Время на вход истекло, попробуйте ещё раз");
        var id = WebAuthn.FromBase64Url(body.RawId ?? body.Id);
        var clientData = WebAuthn.FromBase64Url(body.Response?.ClientDataJSON);
        var authData = WebAuthn.FromBase64Url(body.Response?.AuthenticatorData);
        var signature = WebAuthn.FromBase64Url(body.Response?.Signature);
        if (id is null || clientData is null || authData is null || signature is null) return Refused();

        await using var c = await db.OpenConnectionAsync();
        var key = await c.QuerySingleOrDefaultAsync<StoredKey>(
            "SELECT user_id AS UserId, public_key AS PublicKey, sign_count AS SignCount FROM user_passkeys WHERE credential_id = @id", new { id });
        if (key is null) return Refused("Этот ключ доступа не найден: войдите по паролю и добавьте ключ заново в «Настройки → Вход и защита»");
        // The device names the account it made the passkey for; it must be the passkey's owner.
        if (WebAuthn.FromBase64Url(body.Response?.UserHandle) is { } handle && !handle.AsSpan().SequenceEqual(key.UserId.ToByteArray()))
            return Refused();

        var user = await users.FindByIdAsync(key.UserId);
        if (user is null || !user.IsActive || !user.OrganizationActive) return Refused("Неверный логин или пароль");
        var (origin, rpId) = Party(ctx, options.Value);
        uint count;
        try
        {
            count = WebAuthn.VerifyAssertion(clientData, authData, signature, key.PublicKey, (uint)key.SignCount, taken.Value.Challenge, origin, rpId);
        }
        catch (Exception e) when (e is WebAuthnException or JsonException)
        {
            logs.CreateLogger("Passkeys").LogWarning("Passkey login of {Login} refused: {Reason}", user.Login, e.Message);
            await Audit.WriteAsync(db, user.Id, user.Login, "login_passkey_failed", null, new { ip = ctx.Connection.RemoteIpAddress?.ToString() }, user.OrganizationId);
            return Refused();
        }
        await c.ExecuteAsync("""
            UPDATE user_passkeys SET sign_count = @count, last_used_at = now() WHERE credential_id = @id;
            UPDATE users SET last_login_at = now() WHERE id = @user;
            """, new { id, count = (long)count, user = user.Id });
        users.Forget(user.Id);
        return await AuthEndpoints.SignInAsync(ctx, user, db, "login", "passkey");
    }

    // ---- the user's own passkeys -------------------------------------------------------------------------------

    private static async Task<IResult> List(ClaimsPrincipal principal, NpgsqlDataSource db)
    {
        await using var c = await db.OpenConnectionAsync();
        var rows = await c.QueryAsync<(byte[] Id, string Name, DateTime CreatedAt, DateTime? LastUsedAt)>(
            "SELECT credential_id, name, created_at, last_used_at FROM user_passkeys WHERE user_id = @id ORDER BY created_at",
            new { id = principal.UserId() });
        return Results.Ok(rows.Select(r => new { id = WebAuthn.ToBase64Url(r.Id), name = r.Name, created_at = r.CreatedAt, last_used_at = r.LastUsedAt }));
    }

    /// <summary>Adding a passkey needs the password again: a forgotten open session must not be enough.</summary>
    private static async Task<IResult> RegisterOptions(PasskeyOptionsRequest body, ClaimsPrincipal principal, HttpContext ctx, UserService users,
        NpgsqlDataSource db, PasskeyChallenges challenges, IOptions<CentralOptions> options)
    {
        var me = await users.FindByIdAsync(principal.UserId());
        if (me is null) return Results.Unauthorized();
        var (check, _) = await users.CheckLoginAsync(me.Login, body.Password ?? "");
        if (check == UserService.LoginResult.Locked) return Results.Problem(statusCode: 423, title: "Слишком много неудачных попыток. Попробуйте через 15 минут.");
        if (check != UserService.LoginResult.Ok) return Results.Problem(statusCode: 400, title: "Пароль неверен");

        await using var c = await db.OpenConnectionAsync();
        var existing = await c.QueryAsync<byte[]>("SELECT credential_id FROM user_passkeys WHERE user_id = @id", new { id = me.Id });
        var (_, rpId) = Party(ctx, options.Value);
        var challenge = WebAuthn.NewChallenge();
        ctx.Response.Cookies.Append(PasskeyChallenges.Cookie, challenges.Issue("register", me.Id, me.SecurityStamp, challenge), ChallengeCookie(ctx, options.Value));
        return Results.Ok(WebAuthnJson(new
        {
            challenge,
            rp = new { id = rpId, name = RpName },
            user = new { id = WebAuthn.ToBase64Url(me.Id.ToByteArray()), name = me.Login, displayName = me.Name },
            pubKeyCredParams = WebAuthn.Algorithms.Select(alg => new { type = "public-key", alg }),
            timeout = TimeoutMs,
            attestation = "none",
            authenticatorSelection = new { residentKey = "required", requireResidentKey = true, userVerification = "required" },
            excludeCredentials = existing.Select(x => new { type = "public-key", id = WebAuthn.ToBase64Url(x) }),
        }));
    }

    private static async Task<IResult> Register(PasskeyRegisterRequest body, ClaimsPrincipal principal, HttpContext ctx, UserService users,
        NpgsqlDataSource db, PasskeyChallenges challenges, IOptions<CentralOptions> options, ILoggerFactory logs)
    {
        var taken = challenges.Take(ctx.Request.Cookies[PasskeyChallenges.Cookie], "register");
        ctx.Response.Cookies.Delete(PasskeyChallenges.Cookie, ChallengeCookie(ctx, options.Value));
        var me = await users.FindByIdAsync(principal.UserId());
        if (me is null) return Results.Unauthorized();
        if (taken is null || taken.Value.UserId != me.Id || taken.Value.Stamp != me.SecurityStamp)
            return Results.Problem(statusCode: 400, title: "Время истекло, начните заново");
        var clientData = WebAuthn.FromBase64Url(body.Credential?.Response?.ClientDataJSON);
        var attestation = WebAuthn.FromBase64Url(body.Credential?.Response?.AttestationObject);
        if (clientData is null || attestation is null) return Results.Problem(statusCode: 400, title: "Устройство не создало ключ доступа");

        var (origin, rpId) = Party(ctx, options.Value);
        WebAuthn.Registration reg;
        try
        {
            reg = WebAuthn.VerifyRegistration(clientData, attestation, taken.Value.Challenge, origin, rpId);
        }
        catch (Exception e) when (e is WebAuthnException or JsonException)
        {
            logs.CreateLogger("Passkeys").LogWarning("Passkey of {Login} refused: {Reason}", me.Login, e.Message);
            return Results.Problem(statusCode: 400, title: "Устройство не подходит: нужен вход по отпечатку, лицу или PIN-коду устройства");
        }
        var name = string.IsNullOrWhiteSpace(body.Name) ? DeviceName(ctx.Request.Headers.UserAgent.ToString()) : body.Name.Trim();
        if (name.Length > 60) name = name[..60];
        await using var c = await db.OpenConnectionAsync();
        var added = await c.ExecuteAsync("""
            INSERT INTO user_passkeys (credential_id, user_id, public_key, sign_count, name)
            VALUES (@id, @user, @key, @count, @name) ON CONFLICT (credential_id) DO NOTHING
            """, new { id = reg.CredentialId, user = me.Id, key = reg.PublicKey, count = (long)reg.SignCount, name });
        if (added == 0) return Results.Problem(statusCode: 409, title: "Этот ключ доступа уже добавлен");
        await Audit.WriteAsync(db, me.Id, me.Login, "passkey_added", "user:" + me.Id, new { name }, me.OrganizationId);
        return Results.Ok(new { id = WebAuthn.ToBase64Url(reg.CredentialId), name });
    }

    /// <summary>Removing a passkey (a lost phone) also ends the other sessions; this one stays.</summary>
    private static async Task<IResult> Delete(string id, ClaimsPrincipal principal, HttpContext ctx, UserService users, NpgsqlDataSource db)
    {
        var me = await users.FindByIdAsync(principal.UserId());
        if (me is null) return Results.Unauthorized();
        var credential = WebAuthn.FromBase64Url(id);
        if (credential is null) return Results.NotFound();
        var stamp = Guid.NewGuid();
        await using var c = await db.OpenConnectionAsync();
        var name = await c.ExecuteScalarAsync<string?>(
            "DELETE FROM user_passkeys WHERE credential_id = @credential AND user_id = @user RETURNING name", new { credential, user = me.Id });
        if (name is null) return Results.NotFound();
        await c.ExecuteAsync("UPDATE users SET security_stamp = @stamp WHERE id = @id", new { id = me.Id, stamp });
        users.Forget(me.Id);
        me.SecurityStamp = stamp;
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, UserService.Principal(me, principal.OrganizationId()));
        await Audit.WriteAsync(db, me.Id, me.Login, "passkey_removed", "user:" + me.Id, new { name }, me.OrganizationId);
        return Results.NoContent();
    }

    /// <summary>A readable default name: «iPhone», «Android», «Windows», «Mac».</summary>
    internal static string DeviceName(string userAgent) =>
        userAgent.Contains("iPhone") ? "iPhone"
        : userAgent.Contains("iPad") ? "iPad"
        : userAgent.Contains("Android") ? "Android"
        : userAgent.Contains("Windows") ? "Windows"
        : userAgent.Contains("Macintosh") || userAgent.Contains("Mac OS") ? "Mac"
        : userAgent.Contains("Linux") ? "Linux"
        : "Устройство";
}
