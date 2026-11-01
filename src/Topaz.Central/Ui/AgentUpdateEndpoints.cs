using System.Security.Claims;
using Dapper;
using Npgsql;
using Topaz.Central.Updates;
using Topaz.Contracts;

namespace Topaz.Central.Ui;

public sealed record AgentUpdateSettingsBody(string? Mode, bool? Paused, int? PilotDelayHours);
public sealed record StationUpdateBody(string? UpdateGroup, bool? UpdateNow);

/// <summary>Owner's control of agent self-update: settings, staged rollout, per-station group and "update now".</summary>
public static class AgentUpdateEndpoints
{
    public static void MapUiAgentUpdates(this RouteGroupBuilder ui)
    {
        var owner = ui.MapGroup("").RequireAuthorization(Policies.Super);
        owner.MapGet("/agent-updates", Get);
        owner.MapPut("/agent-updates", Save);
        owner.MapPost("/agent-updates/release-now", ReleaseNow);
        owner.MapPost("/agent-updates/rescan", async (AgentReleases releases, CancellationToken ct) => { await releases.ScanAsync(ct); return Results.NoContent(); });
        owner.MapPatch("/stations/{id:guid}/update", SetStation);
    }

    private static async Task<IResult> Get(NpgsqlDataSource db, AgentReleases releases)
    {
        await using var c = await db.OpenConnectionAsync();
        var settings = await c.QuerySingleAsync("SELECT mode, paused, pilot_delay_hours, release_now_version, updated_by, updated_at FROM agent_update_settings WHERE id = 1");
        var known = (await c.QueryAsync("SELECT version, rid, size, first_seen_at, present FROM agent_releases ORDER BY first_seen_at DESC")).ToList();
        var latest = releases.All.Select(p => p.Version).OrderByDescending(v => v, Comparer<string>.Create(SemVer.Compare)).FirstOrDefault();
        var stations = (await c.QueryAsync("""
            SELECT s.id, COALESCE(NULLIF(s.display_name, ''), s.name) AS name, s.agent_version, s.agent_rid, s.update_group, s.update_now, s.last_seen_at,
                   u.version AS report_version, u.status AS report_status, u.error AS report_error, u.at AS report_at
            FROM stations s
            LEFT JOIN LATERAL (SELECT * FROM agent_update_reports r WHERE r.station_id = s.id ORDER BY r.at DESC LIMIT 1) u ON true
            WHERE s.status = 'active' ORDER BY name
            """)).ToList();
        var onLatest = latest is null ? 0 : stations.Count(s => SemVer.Compare((string?)s.agent_version, latest) >= 0);
        var firstSeen = latest is null ? null : known.Where(k => (string)k.version == latest).Select(k => (DateTime?)k.first_seen_at).Min();
        return Results.Ok(new
        {
            settings,
            latest,
            latest_first_seen_at = firstSeen,
            packages = releases.All.Select(p => new { p.Version, p.Rid, p.Manifest.Size }),
            releases = known,
            stations,
            progress = new { total = stations.Count, on_latest = onLatest, pilots = stations.Count(s => (string)s.update_group == "pilot") },
        });
    }

    private static async Task<IResult> Save(AgentUpdateSettingsBody body, ClaimsPrincipal user, NpgsqlDataSource db)
    {
        if (body.Mode is not (null or "auto" or "off")) return Results.Problem(statusCode: 400, title: "Режим: auto или off");
        if (body.PilotDelayHours is < 0 or > 720) return Results.Problem(statusCode: 400, title: "Задержка — от 0 до 720 часов");
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync("""
            UPDATE agent_update_settings SET mode = COALESCE(@Mode, mode), paused = COALESCE(@Paused, paused),
                pilot_delay_hours = COALESCE(@PilotDelayHours, pilot_delay_hours), updated_by = @by, updated_at = now()
            WHERE id = 1
            """, new { body.Mode, body.Paused, body.PilotDelayHours, by = user.Identity?.Name });
        await Audit.WriteAsync(db, user, "agent_updates_settings", "agent_updates", body);
        return Results.NoContent();
    }

    /// <summary>Skips the pilot wait for the newest version (pilot failures no longer block it either).</summary>
    private static async Task<IResult> ReleaseNow(ClaimsPrincipal user, NpgsqlDataSource db, AgentReleases releases)
    {
        var latest = releases.All.Select(p => p.Version).OrderByDescending(v => v, Comparer<string>.Create(SemVer.Compare)).FirstOrDefault();
        if (latest is null) return Results.Problem(statusCode: 409, title: "На сервере нет пакетов обновления агента");
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync("UPDATE agent_update_settings SET release_now_version = @latest, updated_by = @by, updated_at = now() WHERE id = 1",
            new { latest, by = user.Identity?.Name });
        await Audit.WriteAsync(db, user, "agent_updates_release_now", "agent_updates", new { version = latest });
        return Results.Ok(new { version = latest });
    }

    private static async Task<IResult> SetStation(Guid id, StationUpdateBody body, ClaimsPrincipal user, NpgsqlDataSource db)
    {
        if (body.UpdateGroup is not (null or "pilot" or "normal" or "excluded")) return Results.Problem(statusCode: 400, title: "Группа: pilot, normal или excluded");
        await using var c = await db.OpenConnectionAsync();
        var n = await c.ExecuteAsync("UPDATE stations SET update_group = COALESCE(@UpdateGroup, update_group), update_now = COALESCE(@UpdateNow, update_now) WHERE id = @id",
            new { id, body.UpdateGroup, body.UpdateNow });
        if (n == 0) return Results.NotFound();
        await Audit.WriteAsync(db, user, "station_update_settings", "station:" + id, body);
        return Results.NoContent();
    }
}
