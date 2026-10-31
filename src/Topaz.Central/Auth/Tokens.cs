using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Topaz.Central.Auth;

public static class Tokens
{
    /// <summary>256-bit random token, URL-safe, recognizable prefix (helps secret scanners).</summary>
    public static string NewStationToken() =>
        "tpz_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public static bool FixedTimeEquals(string? expected, string? actual)
    {
        if (string.IsNullOrEmpty(expected) || actual is null) return false;
        return CryptographicOperations.FixedTimeEquals(Hash(expected), Hash(actual));
    }

    public static string? Bearer(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
    }
}

public sealed record StationIdentity(Guid Id, string Name);

/// <summary>Resolves the calling station from its bearer token (hash lookup, short cache).</summary>
public sealed class StationAuthenticator(NpgsqlDataSource db, IMemoryCache cache)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    public async Task<StationIdentity?> AuthenticateAsync(HttpContext ctx)
    {
        var token = Tokens.Bearer(ctx);
        if (string.IsNullOrEmpty(token) || token.Length > 200) return null;
        var hash = Tokens.Hash(token);
        var cacheKey = "st:" + Convert.ToHexString(hash);
        if (cache.TryGetValue(cacheKey, out StationIdentity? cached)) return cached;

        await using var conn = await db.OpenConnectionAsync(ctx.RequestAborted);
        var station = await conn.QuerySingleOrDefaultAsync<StationIdentity>(
            "SELECT id, name FROM stations WHERE token_hash = @hash AND status = 'active'", new { hash });
        if (station is not null) cache.Set(cacheKey, station, CacheFor);
        return station;
    }

    /// <summary>Called on revoke/reset so the change applies immediately on this instance.</summary>
    public void Forget(byte[]? tokenHash)
    {
        if (tokenHash is not null) cache.Remove("st:" + Convert.ToHexString(tokenHash));
    }
}

public sealed class AdminAuthFilter(IOptions<CentralOptions> options) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!Tokens.FixedTimeEquals(options.Value.AdminToken, Tokens.Bearer(context.HttpContext)))
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Admin token required");
        return await next(context);
    }
}

public sealed class StationAuthFilter(StationAuthenticator auth) : IEndpointFilter
{
    public const string ItemKey = "topaz.station";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var station = await auth.AuthenticateAsync(context.HttpContext);
        if (station is null)
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unknown or revoked station token");
        context.HttpContext.Items[ItemKey] = station;
        return await next(context);
    }
}

public static class HttpContextStationExtensions
{
    public static StationIdentity Station(this HttpContext ctx) => (StationIdentity)ctx.Items[StationAuthFilter.ItemKey]!;
}
