using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Topaz.Central.Monitoring;
using Topaz.Contracts;

namespace Topaz.Central.Processing;

/// <summary>Wakes the processor when a new batch arrives (polling is the fallback).</summary>
public sealed class BatchSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        try { if (_signal.CurrentCount == 0) _signal.Release(); } catch (SemaphoreFullException) { }
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}

/// <summary>
/// Applies received raw batches to records / record_versions / normalized tables.
/// Per station, batches are applied strictly in arrival order; a batch that keeps failing is parked
/// as 'failed' and blocks only its own station (visible in metrics and the admin API) until an
/// admin reprocesses it, so no later state silently overtakes it.
/// </summary>
public sealed class BatchProcessor(NpgsqlDataSource db, BatchSignal signal, RollupRefresher rollups, IOptions<CentralOptions> options, ILogger<BatchProcessor> log)
    : BackgroundService
{
    // The head (oldest unprocessed) batch of each station; stations take turns (round robin after @last), so a
    // station sending a long history does not hold up the others. A 'failed' head blocks only its own station.
    private const string NextBatchSql = """
        WITH heads AS (
            SELECT DISTINCT ON (station_id) id, station_id, status
            FROM raw_batches WHERE status <> 'processed'
            ORDER BY station_id, id
        )
        SELECT b.id, b.station_id AS StationId, b.payload::text AS Payload, b.attempts, b.received_at AS ReceivedAt
        FROM heads h JOIN raw_batches b ON b.id = h.id
        WHERE h.status = 'received'
        ORDER BY h.station_id <= @last, h.station_id
        LIMIT 1
        FOR UPDATE OF b SKIP LOCKED
        """;

    private Guid _last = Guid.Empty;

    private sealed record PendingBatch(long Id, Guid StationId, string? Payload, int Attempts, DateTime ReceivedAt);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool didWork;
            try
            {
                didWork = await ProcessNextAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "Batch processor loop error");
                didWork = false;
            }
            if (!didWork)
            {
                try { await signal.WaitAsync(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>Processes one batch. Returns false when there was nothing to do.</summary>
    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var batch = await conn.QuerySingleOrDefaultAsync<PendingBatch>(new CommandDefinition(NextBatchSql, new { last = _last }, tx, cancellationToken: ct));
        if (batch is null) return false;
        _last = batch.StationId;

        var sw = Stopwatch.StartNew();
        try
        {
            await ApplyAsync(conn, tx, batch, options.Value.HoldDeletesAfterDays, ct);
            await conn.ExecuteAsync("UPDATE raw_batches SET status = 'processed', processed_at = now(), error = NULL WHERE id = @Id", new { batch.Id }, tx);
            await tx.CommitAsync(ct);
            AppMetrics.ProcessingSeconds.Observe(sw.Elapsed.TotalSeconds);
            rollups.Notify();
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await tx.RollbackAsync(CancellationToken.None);
            AppMetrics.ProcessingFailures.Inc();
            var attempts = batch.Attempts + 1;
            var parked = attempts >= options.Value.MaxProcessingAttempts;
            log.LogError(ex, "Batch {Id} of station {Station} failed (attempt {Attempt}){Parked}",
                batch.Id, batch.StationId, attempts, parked ? "; parked as failed" : "");
            await using var c2 = await db.OpenConnectionAsync(CancellationToken.None);
            await c2.ExecuteAsync("UPDATE raw_batches SET attempts = @attempts, error = @error, status = @status WHERE id = @Id",
                new { batch.Id, attempts, error = ex.GetBaseException().Message, status = parked ? "failed" : "received" });
            if (!parked) await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 2 << attempts)), ct);
            return true;
        }
    }

    private static async Task ApplyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, PendingBatch batch, int holdDeletesAfterDays, CancellationToken ct)
    {
        if (batch.Payload is null) throw new InvalidOperationException("Payload was purged before processing");
        var envelope = JsonSerializer.Deserialize<BatchEnvelope>(batch.Payload, Protocol.Json)
                       ?? throw new InvalidOperationException("Empty payload");
        var dirty = new DirtyDays();
        var anyHeld = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM held_deletes WHERE station_id = @station)", new { station = batch.StationId }, tx, cancellationToken: ct));

        foreach (var item in envelope.Items)
        {
            Projections.ByDataset.TryGetValue(item.Dataset, out var projection);
            if (item.Op == Protocol.OpUpsert)
            {
                var data = item.Data!.ToJsonString();
                // The row is back in Topaz (e.g. the database was restored from a copy): its delete is no longer held.
                if (anyHeld)
                    await conn.ExecuteAsync(new CommandDefinition(
                        "DELETE FROM held_deletes WHERE station_id = @station AND dataset = @dataset AND key = @key",
                        new { station = batch.StationId, dataset = item.Dataset, key = item.Key }, tx, cancellationToken: ct));
                // History keeps the state being replaced (records holds the current one): no copy of every row.
                await ArchiveAsync(conn, tx, batch, item, "(records.hash IS DISTINCT FROM @hash OR records.deleted)", ct);
                var version = await conn.QuerySingleOrDefaultAsync<int?>(new CommandDefinition("""
                    INSERT INTO records (station_id, dataset, key, session_id, hash, data, last_batch_id)
                    VALUES (@station, @dataset, @key, @session, @hash, @data::jsonb, @batch)
                    ON CONFLICT (station_id, dataset, key) DO UPDATE SET
                        session_id = EXCLUDED.session_id, hash = EXCLUDED.hash, data = EXCLUDED.data,
                        deleted = false, version = records.version + 1, updated_at = now(), last_batch_id = EXCLUDED.last_batch_id
                    WHERE records.hash IS DISTINCT FROM EXCLUDED.hash OR records.deleted
                    RETURNING version
                    """, new { station = batch.StationId, dataset = item.Dataset, key = item.Key, session = item.SessionId, hash = item.Hash, data, batch = batch.Id },
                    tx, cancellationToken: ct));
                if (version is null) continue; // same content already stored (re-sent after agent state loss)

                dirty.Add(item.Dataset, item.Data);
                if (projection is not null)
                    await conn.ExecuteAsync(new CommandDefinition(projection.UpsertSql, new { station = batch.StationId, data }, tx, cancellationToken: ct));
                AppMetrics.RecordsApplied.WithLabels("upsert").Inc();
            }
            else
            {
                if (holdDeletesAfterDays > 0 && item.SessionId is not null && await HoldAsync(conn, tx, batch, item, holdDeletesAfterDays, ct))
                {
                    anyHeld = true;
                    AppMetrics.RecordsApplied.WithLabels("delete_held").Inc();
                    continue;
                }
                await ArchiveAsync(conn, tx, batch, item, "NOT records.deleted", ct);
                var deleted = await conn.QuerySingleOrDefaultAsync<DeletedRow>(new CommandDefinition("""
                    UPDATE records SET deleted = true, version = version + 1, updated_at = now(), last_batch_id = @batch
                    WHERE station_id = @station AND dataset = @dataset AND key = @key AND NOT deleted
                    RETURNING version, data::text AS data
                    """, new { station = batch.StationId, dataset = item.Dataset, key = item.Key, batch = batch.Id }, tx, cancellationToken: ct));
                if (deleted is null) continue;

                if (deleted.Data is not null) dirty.Add(item.Dataset, System.Text.Json.Nodes.JsonNode.Parse(deleted.Data)?.AsObject());
                if (projection is not null && deleted.Data is not null)
                    await conn.ExecuteAsync(new CommandDefinition(projection.DeleteSql, new { station = batch.StationId, data = deleted.Data }, tx, cancellationToken: ct));
                AppMetrics.RecordsApplied.WithLabels("delete").Inc();
            }
        }
        await dirty.SaveAsync(conn, tx, batch.StationId, ct);
    }

    /// <summary>Collects the UTC days whose daily rollups must be recomputed (see RollupRefresher).</summary>
    private sealed class DirtyDays
    {
        private readonly HashSet<(string Kind, DateTime Day)> _days = new();
        private readonly HashSet<long> _receipts = new();

        public void Add(string dataset, System.Text.Json.Nodes.JsonObject? data)
        {
            if (data is null) return;
            switch (dataset)
            {
                case "flOrders": AddTs("fuel", data["DateTime"]); break;
                case "trFastTradeH": AddTs("shop", data["DateDoc"]); break;
                case "trFastTradeD":
                    if (data["FastTradeHID"] is { } h && long.TryParse(h.ToString(), out var id)) _receipts.Add(id);
                    break;
            }
        }

        private void AddTs(string kind, System.Text.Json.Nodes.JsonNode? ts)
        {
            if (ts is not null && DateTimeOffset.TryParse(ts.ToString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var at))
                _days.Add((kind, at.UtcDateTime.Date));
        }

        public async Task SaveAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid station, CancellationToken ct)
        {
            if (_receipts.Count > 0)
            {
                var dates = await conn.QueryAsync<DateTime>(new CommandDefinition(
                    "SELECT DISTINCT (doc_at AT TIME ZONE 'UTC')::date FROM shop_receipts WHERE station_id = @station AND receipt_id = ANY(@ids) AND doc_at IS NOT NULL",
                    new { station, ids = _receipts.ToArray() }, tx, cancellationToken: ct));
                foreach (var d in dates) _days.Add(("shop", d));
            }
            if (_days.Count == 0) return;
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO rollup_dirty (station_id, kind, utc_date) SELECT @station, k, d FROM unnest(@kinds, @dates::date[]) AS t(k, d)",
                new { station, kinds = _days.Select(x => x.Kind).ToArray(), dates = _days.Select(x => x.Day).ToArray() }, tx, cancellationToken: ct));
        }
    }

    private sealed record DeletedRow(int Version, string? Data);

    /// <summary>
    /// A row of a shift closed long ago vanished from Topaz: most likely the database was compacted ("свёртка"),
    /// not a sale undone. The delete is kept aside (held_deletes, alert history_deleted) instead of erasing history.
    /// </summary>
    private static async Task<bool> HoldAsync(NpgsqlConnection conn, NpgsqlTransaction tx, PendingBatch batch, RecordChange item, int days, CancellationToken ct) =>
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO held_deletes (station_id, dataset, key, session_id, raw_batch_id)
            SELECT @station, @dataset, @key, @session, @batch
            FROM shifts s
            WHERE s.station_id = @station AND s.session_id = @session
              AND s.ended_at < @received - make_interval(days => @days)
              AND EXISTS (SELECT 1 FROM records r WHERE r.station_id = @station AND r.dataset = @dataset AND r.key = @key AND NOT r.deleted)
            ON CONFLICT (station_id, dataset, key) DO UPDATE SET session_id = EXCLUDED.session_id, raw_batch_id = EXCLUDED.raw_batch_id
            """, new { station = batch.StationId, dataset = item.Dataset, key = item.Key, session = item.SessionId, batch = batch.Id,
                       received = DateTime.SpecifyKind(batch.ReceivedAt, DateTimeKind.Utc), days },
            tx, cancellationToken: ct)) > 0;

    /// <summary>
    /// Moves the current state of the row to record_versions before it is replaced (a changed row or a delete):
    /// record_versions holds every earlier state, records the current one. A deleted state is kept without data
    /// (the data is in the version before it). changed_at is when that state arrived.
    /// </summary>
    private static Task ArchiveAsync(NpgsqlConnection conn, NpgsqlTransaction tx, PendingBatch batch, RecordChange item, string when, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition($"""
            INSERT INTO record_versions (station_id, dataset, key, version, op, hash, data, raw_batch_id, changed_at)
            SELECT records.station_id, records.dataset, records.key, records.version,
                   CASE WHEN records.deleted THEN 'delete' ELSE 'upsert' END,
                   CASE WHEN records.deleted THEN NULL ELSE records.hash END,
                   CASE WHEN records.deleted THEN NULL ELSE records.data END,
                   records.last_batch_id, records.updated_at
            FROM records
            WHERE records.station_id = @station AND records.dataset = @dataset AND records.key = @key AND {when}
            """, new { station = batch.StationId, dataset = item.Dataset, key = item.Key, hash = item.Hash }, tx, cancellationToken: ct));
}
