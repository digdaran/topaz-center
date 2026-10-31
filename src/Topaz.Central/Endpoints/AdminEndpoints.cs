using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;
using Topaz.Central.Auth;
using Topaz.Central.Processing;
using Topaz.Contracts;

namespace Topaz.Central.Endpoints;

public static partial class AdminEndpoints
{
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}$")]
    private static partial Regex DatasetName();

    public static void MapAdmin(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup(Protocol.ApiPrefix).AddEndpointFilter<AdminAuthFilter>();

        admin.MapGet("/admin/stations", ListStations);
        admin.MapPost("/admin/stations/{id:guid}/reset", (Guid id, NpgsqlDataSource db, StationAuthenticator auth) => SetStatus(id, "reenroll", db, auth));
        admin.MapPost("/admin/stations/{id:guid}/revoke", (Guid id, NpgsqlDataSource db, StationAuthenticator auth) => SetStatus(id, "revoked", db, auth));
        admin.MapGet("/admin/batches", ListBatches);
        admin.MapPost("/admin/batches/{id:long}/reprocess", Reprocess);
        admin.MapGet("/export/datasets", ListDatasets);
        admin.MapGet("/export/{dataset}", Export);
    }

    private static async Task<IResult> ListStations(NpgsqlDataSource db)
    {
        await using var conn = await db.OpenConnectionAsync();
        var rows = await conn.QueryAsync("""
            SELECT id, name, status, agent_version, machine_name, last_seen_at, extract(epoch FROM silent_for)::bigint AS silent_seconds,
                   agent_queue_pending, agent_queue_dead, topaz_db_ok, last_collect_error, server_backlog, server_failed
            FROM v_station_status ORDER BY name
            """);
        return Results.Ok(rows);
    }

    /// <summary>reset: the next enrollment with this name gets a new token (agent reinstall); revoke: blocked for good.</summary>
    private static async Task<IResult> SetStatus(Guid id, string status, NpgsqlDataSource db, StationAuthenticator auth)
    {
        await using var conn = await db.OpenConnectionAsync();
        var oldHash = await conn.QuerySingleOrDefaultAsync<byte[]>(
            "UPDATE stations s SET status = @status, token_hash = NULL FROM stations o WHERE s.id = o.id AND s.id = @id RETURNING o.token_hash",
            new { id, status });
        var exists = await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM stations WHERE id = @id)", new { id });
        if (!exists) return Results.NotFound();
        auth.Forget(oldHash);
        return Results.Ok(new { id, status });
    }

    private static async Task<IResult> ListBatches(NpgsqlDataSource db, string status = "failed", int limit = 100)
    {
        if (status is not ("received" or "processed" or "failed")) return Results.BadRequest();
        await using var conn = await db.OpenConnectionAsync();
        var rows = await conn.QueryAsync("""
            SELECT b.id, s.name AS station, b.batch_id, b.seq, b.item_count, b.received_at, b.status, b.attempts, b.error
            FROM raw_batches b JOIN stations s ON s.id = b.station_id
            WHERE b.status = @status ORDER BY b.id DESC LIMIT @limit
            """, new { status, limit = Math.Clamp(limit, 1, 1000) });
        return Results.Ok(rows);
    }

    private static async Task<IResult> Reprocess(long id, NpgsqlDataSource db, BatchSignal signal)
    {
        await using var conn = await db.OpenConnectionAsync();
        var n = await conn.ExecuteAsync("UPDATE raw_batches SET status = 'received', attempts = 0, error = NULL WHERE id = @id AND status = 'failed'", new { id });
        signal.Notify();
        return n == 0 ? Results.NotFound() : Results.Ok(new { id, status = "received" });
    }

    private static async Task<IResult> ListDatasets(NpgsqlDataSource db)
    {
        await using var conn = await db.OpenConnectionAsync();
        var rows = await conn.QueryAsync("""
            SELECT dataset, count(*) FILTER (WHERE NOT deleted) AS rows, count(DISTINCT station_id) AS stations, max(updated_at) AS last_update
            FROM records GROUP BY dataset ORDER BY dataset
            """);
        return Results.Ok(rows);
    }

    /// <summary>
    /// Streams the current state of a dataset as NDJSON (default) or CSV.
    /// Incremental pulls: pass updated_since = the max updated_at of the previous export.
    /// </summary>
    private static async Task Export(HttpContext ctx, NpgsqlDataSource db, string dataset, Guid? station_id, DateTimeOffset? updated_since,
        bool include_deleted = false, string format = "ndjson", int limit = 100_000)
    {
        if (!DatasetName().IsMatch(dataset) || format is not ("ndjson" or "csv"))
        {
            ctx.Response.StatusCode = 400;
            return;
        }
        ctx.Response.ContentType = format == "csv" ? "text/csv; charset=utf-8" : "application/x-ndjson; charset=utf-8";
        ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{dataset}.{(format == "csv" ? "csv" : "ndjson")}\"";

        await using var conn = await db.OpenConnectionAsync(ctx.RequestAborted);
        await using var cmd = new NpgsqlCommand("""
            SELECT s.name, r.station_id, r.key, r.session_id, r.deleted, r.version, r.updated_at, r.data::text
            FROM records r JOIN stations s ON s.id = r.station_id
            WHERE r.dataset = @dataset
              AND (@station::uuid IS NULL OR r.station_id = @station)
              AND (@since::timestamptz IS NULL OR r.updated_at > @since)
              AND (@deleted OR NOT r.deleted)
            ORDER BY r.updated_at, r.station_id, r.key
            LIMIT @limit
            """, conn);
        cmd.Parameters.AddWithValue("dataset", dataset);
        cmd.Parameters.Add(new NpgsqlParameter("station", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)station_id ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("since", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)updated_since?.ToUniversalTime() ?? DBNull.Value });
        cmd.Parameters.AddWithValue("deleted", include_deleted);
        cmd.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 1_000_000));

        await using var reader = await cmd.ExecuteReaderAsync(ctx.RequestAborted);
        await using var writer = new StreamWriter(ctx.Response.Body, new UTF8Encoding(false));
        string[]? columns = null;
        while (await reader.ReadAsync(ctx.RequestAborted))
        {
            var meta = new
            {
                station = reader.GetString(0),
                station_id = reader.GetGuid(1),
                key = reader.GetString(2),
                session_id = reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3),
                deleted = reader.GetBoolean(4),
                version = reader.GetInt32(5),
                updated_at = reader.GetFieldValue<DateTimeOffset>(6),
            };
            var data = reader.IsDBNull(7) ? "{}" : reader.GetString(7);
            if (format == "ndjson")
            {
                var metaJson = JsonSerializer.Serialize(meta);
                await writer.WriteAsync(metaJson[..^1] + ",\"data\":" + data + "}\n");
                continue;
            }
            using var doc = JsonDocument.Parse(data);
            if (columns is null)
            {
                // CSV columns: metadata + fields of the first row (Topaz rows of one table share columns).
                columns = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
                await writer.WriteAsync("station;station_id;key;session_id;deleted;version;updated_at;" + string.Join(';', columns.Select(Csv)) + "\n");
            }
            var values = new List<string>
            {
                Csv(meta.station), meta.station_id.ToString(), Csv(meta.key), meta.session_id?.ToString(CultureInfo.InvariantCulture) ?? "",
                meta.deleted ? "1" : "0", meta.version.ToString(CultureInfo.InvariantCulture), meta.updated_at.ToString("O", CultureInfo.InvariantCulture),
            };
            foreach (var col in columns)
                values.Add(doc.RootElement.TryGetProperty(col, out var v) ? Csv(v.ValueKind switch
                {
                    JsonValueKind.Null => "",
                    JsonValueKind.String => v.GetString()!,
                    _ => v.GetRawText(),
                }) : "");
            await writer.WriteAsync(string.Join(';', values) + "\n");
        }
        await writer.FlushAsync(ctx.RequestAborted);
    }

    // Semicolon-separated (opens correctly in Russian-locale Excel), RFC 4180 quoting.
    private static string Csv(string s) =>
        s.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
