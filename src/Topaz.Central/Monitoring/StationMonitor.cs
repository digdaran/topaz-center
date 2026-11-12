using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Topaz.Central.Monitoring;

/// <summary>
/// Every 30 s: refreshes per-station gauges, warns about silent stations, removes a portion of old duplicate
/// history rows (until done); hourly: retention cleanup.
/// </summary>
public sealed class StationMonitor(NpgsqlDataSource db, IOptions<CentralOptions> options, ILogger<StationMonitor> log) : BackgroundService
{
    private sealed record Row(string Name, double? SilentSeconds, int? QueuePending, int? QueueDead, string Status);

    private readonly HashSet<string> _stale = new(StringComparer.Ordinal);
    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;
    private bool _dedupDone;

    /// <summary>History rows checked per round by the duplicate cleanup (by id range: a bounded, index-only cost).</summary>
    internal const int DedupRange = 20_000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                await RefreshAsync(stoppingToken);
                if (!_dedupDone) _dedupDone = await DedupVersionsAsync(stoppingToken);
                if (DateTimeOffset.UtcNow - _lastCleanup > TimeSpan.FromHours(1))
                {
                    await CleanupAsync(stoppingToken);
                    _lastCleanup = DateTimeOffset.UtcNow;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning(ex, "Station monitor iteration failed"); }
        } while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Row>("""
            SELECT name, extract(epoch FROM silent_for)::float8 AS SilentSeconds,
                   agent_queue_pending AS QueuePending, agent_queue_dead AS QueueDead, status
            FROM v_station_status
            """);
        var limit = TimeSpan.FromMinutes(options.Value.StationSilentMinutes).TotalSeconds;
        foreach (var r in rows.Where(r => r.Status == "active"))
        {
            AppMetrics.StationSilentSeconds.WithLabels(r.Name).Set(r.SilentSeconds ?? double.NaN);
            AppMetrics.StationAgentQueue.WithLabels(r.Name).Set(r.QueuePending ?? 0);
            AppMetrics.StationAgentDead.WithLabels(r.Name).Set(r.QueueDead ?? 0);
            var stale = r.SilentSeconds is null || r.SilentSeconds > limit;
            if (stale && _stale.Add(r.Name))
                log.LogWarning("Station {Station} is silent for {Minutes:F0} min", r.Name, (r.SilentSeconds ?? 0) / 60);
            else if (!stale && _stale.Remove(r.Name))
                log.LogInformation("Station {Station} is back online", r.Name);
        }
        var (backlog, failed) = await conn.QuerySingleAsync<(long, long)>(
            "SELECT count(*) FILTER (WHERE status = 'received'), count(*) FILTER (WHERE status = 'failed') FROM raw_batches WHERE status <> 'processed'");
        AppMetrics.ServerBacklog.Set(backlog);
        AppMetrics.ServerFailed.Set(failed);
        if (failed > 0) log.LogWarning("{Failed} batch(es) parked as failed; see GET /api/v1/admin/batches", failed);
    }

    private async Task CleanupAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE raw_batches SET payload = NULL
            WHERE status = 'processed' AND payload IS NOT NULL AND received_at < now() - make_interval(days => @days)
            """, new { days = options.Value.RawRetentionDays }, cancellationToken: ct, commandTimeout: 600));
        if (n > 0) log.LogInformation("Retention: cleared payload of {Count} processed raw batch(es)", n);
    }

    /// <summary>
    /// Before 1.10.1 record_versions also stored a copy of the current state of every row (as much as records).
    /// Walks those old rows by id in ranges and deletes the ones equal to the current version in records;
    /// earlier states stay. Progress is kept in maintenance_state, so a restart continues where it stopped.
    /// Returns true when everything written before the change was checked.
    /// </summary>
    internal async Task<bool> DedupVersionsAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var state = await conn.QuerySingleOrDefaultAsync<string>("SELECT value FROM maintenance_state WHERE key = 'versions_dedup'");
        if (state is null or "done") return true;
        var parts = state.Split(';').Select(p => p.Split(':')).ToDictionary(p => p[0], p => long.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture));
        long upTo = parts["up_to"], cursor = parts["cursor"];
        var to = Math.Min(cursor + DedupRange, upTo);
        var n = await conn.ExecuteAsync(new CommandDefinition("""
            DELETE FROM record_versions v USING records r
            WHERE v.id > @cursor AND v.id <= @to
              AND r.station_id = v.station_id AND r.dataset = v.dataset AND r.key = v.key AND r.version = v.version
            """, new { cursor, to }, cancellationToken: ct, commandTimeout: 120));
        var done = to >= upTo;
        await conn.ExecuteAsync("UPDATE maintenance_state SET value = @v WHERE key = 'versions_dedup'",
            new { v = done ? "done" : $"up_to:{upTo};cursor:{to}" });
        if (n > 0 || done) log.LogInformation("History cleanup: removed {Count} duplicate row(s) up to id {To} of {UpTo}{Done}", n, to, upTo, done ? " (done)" : "");
        return done;
    }
}
