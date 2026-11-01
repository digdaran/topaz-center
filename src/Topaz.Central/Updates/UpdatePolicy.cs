using Dapper;
using Npgsql;
using Topaz.Contracts;

namespace Topaz.Central.Updates;

/// <summary>
/// Decides in each heartbeat whether to offer the newest package. Pilot stations (and "update now") get it at once;
/// the others after pilot_delay_hours since the version appeared, provided no pilot failed on it and, if there are
/// pilots, at least one succeeded. "Update all now" (release_now_version) skips the wait. Excluded stations never.
/// </summary>
public static class UpdatePolicy
{
    private sealed record Settings(string Mode, bool Paused, int PilotDelayHours, string? ReleaseNowVersion);
    private sealed record StationRow(string UpdateGroup, bool UpdateNow);
    private sealed record Pilots(long Total, long Succeeded, long Failed, DateTime? FirstSeenAt);

    public static async Task<AgentUpdateOffer?> DecideAsync(NpgsqlConnection c, AgentReleases releases, Guid stationId, string agentVersion, string? rid)
    {
        var latest = releases.Latest(rid);
        if (latest is null || SemVer.Compare(latest.Version, agentVersion) <= 0) return null;

        var settings = await c.QuerySingleAsync<Settings>("SELECT mode AS Mode, paused AS Paused, pilot_delay_hours AS PilotDelayHours, release_now_version AS ReleaseNowVersion FROM agent_update_settings WHERE id = 1");
        var station = await c.QuerySingleAsync<StationRow>("SELECT update_group AS UpdateGroup, update_now AS UpdateNow FROM stations WHERE id = @stationId", new { stationId });
        var offer = new AgentUpdateOffer { Version = latest.Version, Rid = latest.Rid };

        if (station.UpdateNow) return offer;                                   // explicit owner's request
        if (settings.Mode != "auto" || settings.Paused || station.UpdateGroup == "excluded") return null;
        if (station.UpdateGroup == "pilot" || settings.ReleaseNowVersion == latest.Version) return offer;

        var p = await c.QuerySingleAsync<Pilots>("""
            SELECT count(*) FILTER (WHERE s.update_group = 'pilot') AS Total,
                   count(*) FILTER (WHERE s.update_group = 'pilot' AND (r.status = 'succeeded' OR s.agent_version = @v)) AS Succeeded,
                   count(*) FILTER (WHERE s.update_group = 'pilot' AND r.status = 'failed') AS Failed,
                   (SELECT min(first_seen_at) FROM agent_releases WHERE version = @v) AS FirstSeenAt
            FROM stations s LEFT JOIN agent_update_reports r ON r.station_id = s.id AND r.version = @v
            WHERE s.status = 'active'
            """, new { v = latest.Version });
        if (p.Failed > 0) return null;
        if (p.Total > 0 && p.Succeeded == 0) return null;
        var since = p.FirstSeenAt is { } t ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : DateTime.UtcNow;
        return DateTime.UtcNow >= since.AddHours(settings.PilotDelayHours) ? offer : null;
    }
}
