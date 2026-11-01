using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Topaz.Central.Auth;
using Topaz.Central.Monitoring;
using Topaz.Central.Processing;
using Topaz.Central.Updates;
using Topaz.Contracts;

namespace Topaz.Central.Endpoints;

public static class IngestEndpoints
{
    private sealed record ExistingStation(Guid Id, string Status);

    public static void MapIngest(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(Protocol.ApiPrefix);

        api.MapPost("/enroll", Enroll).RequireRateLimiting("enroll");

        var station = api.MapGroup("").AddEndpointFilter<StationAuthFilter>().RequireRateLimiting("station");
        station.MapPost("/batches", ReceiveBatch);
        station.MapPost("/heartbeat", Heartbeat);
        station.MapGet("/agent/updates/{version}/{rid}/{file}", DownloadUpdate);
    }

    /// <summary>
    /// Station PC health: clock skew = the PC's local wall clock (what Topaz writes) minus the true time in the
    /// station's zone, so a wrong Windows time zone shows up too; plus one row per day for disk/DB trends.
    /// </summary>
    private static async Task SaveHostAsync(NpgsqlConnection conn, Guid stationId, HostInfo host)
    {
        DateTime? local = DateTime.TryParseExact(host.LocalClock, "yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var l) ? l : null;
        await conn.ExecuteAsync("""
            UPDATE stations SET host_checked_at = now(),
                clock_skew_seconds = CASE WHEN @local::timestamp IS NULL THEN NULL
                                          ELSE round(extract(epoch FROM @local::timestamp - (now() AT TIME ZONE time_zone))) END
            WHERE id = @stationId;
            INSERT INTO station_host_daily (station_id, day, db_size, db_disk_free, db_disk_total, sys_disk_free, sys_disk_total)
            SELECT @stationId, (now() AT TIME ZONE time_zone)::date, @DbFileBytes, @DbDiskFreeBytes, @DbDiskTotalBytes, @SystemDiskFreeBytes, @SystemDiskTotalBytes
            FROM stations WHERE id = @stationId
            ON CONFLICT (station_id, day) DO UPDATE SET db_size = EXCLUDED.db_size, db_disk_free = EXCLUDED.db_disk_free,
                db_disk_total = EXCLUDED.db_disk_total, sys_disk_free = EXCLUDED.sys_disk_free, sys_disk_total = EXCLUDED.sys_disk_total;
            """, new { stationId, local, host.DbFileBytes, host.DbDiskFreeBytes, host.DbDiskTotalBytes, host.SystemDiskFreeBytes, host.SystemDiskTotalBytes });
    }

    /// <summary>Files of a signed agent package shipped with this server (manifest, signature, zip).</summary>
    internal static IResult DownloadUpdate(string version, string rid, string file, AgentReleases releases)
    {
        if (releases.Find(version, rid) is not { } p) return Results.NotFound();
        var allowed = new[] { AgentUpdateManifest.FileName, AgentUpdateManifest.SignatureFileName, p.Manifest.File };
        if (!allowed.Contains(file, StringComparer.Ordinal)) return Results.NotFound();
        var contentType = file.EndsWith(".json", StringComparison.Ordinal) ? "application/json" : "application/octet-stream";
        return Results.File(Path.Combine(p.Directory, file), contentType, enableRangeProcessing: true);
    }

    /// <summary>
    /// First contact of a newly installed agent: station name + shared install key → personal token.
    /// A station that already has an active token cannot be taken over; an admin must reset it first.
    /// </summary>
    internal static async Task<IResult> Enroll(EnrollRequest body, HttpContext ctx, NpgsqlDataSource db,
        IOptions<CentralOptions> options, ILogger<EnrollRequest> log)
    {
        if (string.IsNullOrEmpty(options.Value.EnrollmentKey))
            return Results.Problem(statusCode: 503, title: "Enrollment is disabled (Topaz__EnrollmentKey not set)");
        if (!Tokens.FixedTimeEquals(options.Value.EnrollmentKey, body.EnrollmentKey))
        {
            log.LogWarning("Enrollment with a wrong key for station {Station} from {Ip}", body.StationName, ctx.Connection.RemoteIpAddress);
            return Results.Problem(statusCode: 403, title: "Invalid enrollment key");
        }
        var name = body.StationName?.Trim() ?? "";
        if (name.Length is 0 or > 100)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["station_name"] = ["Station name must be 1..100 characters."] });

        var normalized = name.ToLowerInvariant();
        var token = Tokens.NewStationToken();
        var hash = Tokens.Hash(token);
        var ip = ctx.Connection.RemoteIpAddress?.ToString();

        await using var conn = await db.OpenConnectionAsync(ctx.RequestAborted);
        await using var tx = await conn.BeginTransactionAsync(ctx.RequestAborted);
        var existing = await conn.QuerySingleOrDefaultAsync<ExistingStation>(
            "SELECT id, status FROM stations WHERE name_normalized = @normalized FOR UPDATE", new { normalized }, tx);

        Guid id;
        if (existing is null)
        {
            id = Guid.NewGuid();
            await conn.ExecuteAsync("""
                INSERT INTO stations (id, name, name_normalized, token_hash, token_issued_at, status, machine_name, agent_version, last_ip, last_seen_at)
                VALUES (@id, @name, @normalized, @hash, now(), 'active', @machine, @version, @ip, now())
                """, new { id, name, normalized, hash, machine = body.MachineName, version = body.AgentVersion, ip }, tx);
            log.LogInformation("Station {Station} enrolled ({Id}) from {Ip}", name, id, ip);
        }
        else if (existing.Status == "reenroll")
        {
            id = existing.Id;
            await conn.ExecuteAsync("""
                UPDATE stations SET token_hash = @hash, token_issued_at = now(), status = 'active',
                    machine_name = @machine, agent_version = @version, last_ip = @ip, last_seen_at = now()
                WHERE id = @id
                """, new { id, hash, machine = body.MachineName, version = body.AgentVersion, ip }, tx);
            log.LogInformation("Station {Station} re-enrolled ({Id}) from {Ip}", name, id, ip);
        }
        else
        {
            return Results.Problem(statusCode: 409,
                title: existing.Status == "revoked" ? "Station is revoked" : "Station is already enrolled",
                detail: "An administrator must reset the station (POST /api/v1/admin/stations/{id}/reset) before a new agent can enroll.");
        }
        await tx.CommitAsync(ctx.RequestAborted);
        return Results.Ok(new EnrollResponse { StationId = id, StationName = name, Token = token });
    }

    /// <summary>
    /// Stores the batch as received and acknowledges it; normalization happens asynchronously.
    /// Idempotent: the same batch_id again → 200 "duplicate"; same batch_id with other content → 409.
    /// </summary>
    internal static async Task<IResult> ReceiveBatch(HttpContext ctx, NpgsqlDataSource db, BatchSignal signal, ILogger<BatchEnvelope> log)
    {
        var station = ctx.Station();
        if (ctx.Request.ContentType is not { } ct || !ct.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            return Results.Problem(statusCode: 415, title: "Content-Type must be application/json");

        // Body is already gunzipped by the request decompression middleware (size-limited).
        byte[] body;
        using (var ms = new MemoryStream())
        {
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            body = ms.ToArray();
        }

        BatchEnvelope? batch;
        try { batch = JsonSerializer.Deserialize<BatchEnvelope>(body, Protocol.Json); }
        catch (JsonException ex)
        {
            AppMetrics.BatchesReceived.WithLabels("rejected").Inc();
            return Results.Problem(statusCode: 400, title: "Malformed JSON", detail: ex.Message);
        }
        var errors = BatchValidator.Validate(batch);
        if (errors.Count > 0)
        {
            AppMetrics.BatchesReceived.WithLabels("rejected").Inc();
            log.LogWarning("Batch from {Station} rejected: {Errors}", station.Name, string.Join("; ", errors.SelectMany(e => e.Value)));
            return Results.ValidationProblem(errors, statusCode: 422);
        }
        var headerKey = ctx.Request.Headers[Protocol.IdempotencyKeyHeader].ToString();
        if (headerKey.Length > 0 && (!Guid.TryParse(headerKey, out var hk) || hk != batch!.BatchId))
            return Results.Problem(statusCode: 400, title: "Idempotency-Key header must equal batch_id");

        var sha = SHA256.HashData(body);
        await using var conn = await db.OpenConnectionAsync(ctx.RequestAborted);
        var inserted = await conn.QuerySingleOrDefaultAsync<long?>("""
            INSERT INTO raw_batches (station_id, batch_id, seq, schema_version, agent_version, created_at, item_count, payload_sha256, payload)
            VALUES (@station, @batchId, @seq, @schema, @agent, @createdAt, @count, @sha, @payload::jsonb)
            ON CONFLICT ON CONSTRAINT ux_raw_batches_idempotency DO NOTHING
            RETURNING id
            """, new
        {
            station = station.Id,
            batchId = batch!.BatchId,
            seq = batch.Seq,
            schema = batch.SchemaVersion,
            agent = batch.AgentVersion,
            createdAt = batch.CreatedAt.ToUniversalTime(),
            count = batch.Items.Count,
            sha,
            payload = System.Text.Encoding.UTF8.GetString(body),
        });

        string status;
        if (inserted is null)
        {
            var existingSha = await conn.QuerySingleAsync<byte[]>(
                "SELECT payload_sha256 FROM raw_batches WHERE station_id = @station AND batch_id = @batchId",
                new { station = station.Id, batchId = batch.BatchId });
            if (!existingSha.AsSpan().SequenceEqual(sha))
            {
                AppMetrics.BatchesReceived.WithLabels("conflict").Inc();
                log.LogError("Batch {BatchId} from {Station} reused with different content", batch.BatchId, station.Name);
                return Results.Problem(statusCode: 409, title: "batch_id already used with different content");
            }
            status = "duplicate";
        }
        else
        {
            status = "accepted";
            signal.Notify();
        }

        await conn.ExecuteAsync("""
            UPDATE stations SET last_seen_at = now(), last_ip = @ip,
                last_batch_seq = GREATEST(COALESCE(last_batch_seq, 0), @seq), agent_version = @agent
            WHERE id = @id
            """, new { id = station.Id, ip = ctx.Connection.RemoteIpAddress?.ToString(), seq = batch.Seq, agent = batch.AgentVersion });

        AppMetrics.BatchesReceived.WithLabels(status).Inc();
        var ack = new BatchAck { Status = status, BatchId = batch.BatchId, ReceivedAt = DateTimeOffset.UtcNow };
        return status == "accepted" ? Results.Json(ack, Protocol.Json, statusCode: 202) : Results.Json(ack, Protocol.Json, statusCode: 200);
    }

    internal static async Task<IResult> Heartbeat(HeartbeatRequest body, HttpContext ctx, NpgsqlDataSource db, RollupRefresher rollups, AgentReleases releases)
    {
        var station = ctx.Station();
        await using var conn = await db.OpenConnectionAsync(ctx.RequestAborted);
        await conn.ExecuteAsync("""
            WITH old AS (SELECT time_zone FROM stations WHERE id = @id),
            upd AS (
                UPDATE stations SET last_seen_at = now(), last_ip = @ip, agent_version = @version,
                    machine_name = COALESCE(@machine, machine_name), last_heartbeat = @hb::jsonb,
                    time_zone = CASE WHEN NOT time_zone_manual AND @tz IN (SELECT name FROM pg_timezone_names) THEN @tz ELSE time_zone END
                WHERE id = @id
                RETURNING time_zone)
            INSERT INTO rollup_dirty (station_id, kind)
            SELECT @id, 'all' FROM old, upd WHERE old.time_zone <> upd.time_zone
            """, new
        {
            id = station.Id,
            ip = ctx.Connection.RemoteIpAddress?.ToString(),
            version = body.AgentVersion,
            machine = body.MachineName,
            hb = JsonSerializer.Serialize(body, Protocol.Json),
            tz = body.TimeZone ?? "",
        });
        rollups.Notify(); // a time zone change marked the station's rollups dirty

        // Self-update: remember the runtime and the reported state, then maybe offer the newest package.
        if (body.Update is { Version.Length: > 0 and <= 64 } u
            && u.Status is AgentUpdateStatus.Downloading or AgentUpdateStatus.Installing or AgentUpdateStatus.Succeeded or AgentUpdateStatus.Failed)
            await conn.ExecuteAsync("""
                INSERT INTO agent_update_reports (station_id, version, status, error, at) VALUES (@id, @v, @status, @error, @at)
                ON CONFLICT (station_id, version) DO UPDATE SET status = EXCLUDED.status, error = EXCLUDED.error, at = EXCLUDED.at
                WHERE agent_update_reports.at <= EXCLUDED.at
                """, new { id = station.Id, v = u.Version, status = u.Status, error = u.Error is { Length: > 1000 } e ? e[..1000] : u.Error, at = u.At });
        if (body.Rid is { Length: > 0 and <= 32 } rid)
            await conn.ExecuteAsync("UPDATE stations SET agent_rid = @rid WHERE id = @id", new { id = station.Id, rid });
        if (body.Host is { } host) await SaveHostAsync(conn, station.Id, host);
        var offer = await UpdatePolicy.DecideAsync(conn, releases, station.Id, body.AgentVersion, body.Rid);
        if (offer is null)
            await conn.ExecuteAsync("UPDATE stations SET update_now = false WHERE id = @id AND update_now", new { id = station.Id }); // done or nothing to install
        return Results.Json(new HeartbeatResponse { ServerTime = DateTimeOffset.UtcNow, Update = offer }, Protocol.Json);
    }
}
