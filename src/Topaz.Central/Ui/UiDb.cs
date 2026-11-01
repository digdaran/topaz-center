using System.Security.Claims;
using Dapper;
using Npgsql;

namespace Topaz.Central.Ui;

/// <summary>
/// The web UI's own connection pool: role topaz_ui (no superuser, so row level security applies) and app.scope=ui
/// (migration 018). Both are startup options, so they survive the pool's DISCARD ALL; the per-request scope does not.
/// </summary>
public sealed record UiDataSource(NpgsqlDataSource Source);

/// <summary>
/// Connections for UI requests: every connection carries the caller's scope (app.stations, app.org, app.platform),
/// so even a query that forgot its filter cannot read another organization's rows. Npgsql resets the settings
/// (DISCARD ALL) when the connection goes back to the pool.
/// </summary>
public sealed class UiDb(UiDataSource ui, NpgsqlDataSource system, IHttpContextAccessor http, StationScope scope)
{
    private Guid[]? _stations;

    /// <summary>The unrestricted pool, for writes that are not data of a station (audit log).</summary>
    public NpgsqlDataSource System => system;

    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        var user = http.HttpContext?.User ?? throw new InvalidOperationException("UiDb is for UI requests only");
        _stations ??= user.Identity?.IsAuthenticated == true ? await scope.AllowedAsync(user) : [];
        var c = await ui.Source.OpenConnectionAsync(ct);
        await c.ExecuteAsync(
            "SELECT set_config('app.stations', @s, false), set_config('app.org', @o, false), set_config('app.platform', @p, false)",
            new
            {
                s = "{" + string.Join(",", _stations) + "}",
                o = user.OrganizationId()?.ToString() ?? "",
                p = user.IsInRole(Roles.Superadmin) ? "on" : "off",
            });
        return c;
    }

    public static NpgsqlDataSource BuildUiSource(string connectionString) =>
        new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(connectionString) { Options = "-c app.scope=ui -c role=topaz_ui", ApplicationName = "topaz-ui" }.ConnectionString).Build();
}

public static class OrganizationClaims
{
    public const string Claim = "org";

    /// <summary>The user's organization (for a superadmin: the one selected in the header).</summary>
    public static Guid? OrganizationId(this ClaimsPrincipal u) => Guid.TryParse(u.FindFirstValue(Claim), out var g) ? g : null;
}
