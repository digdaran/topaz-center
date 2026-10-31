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
public sealed class BatchProcessor(NpgsqlDataSource db, BatchSignal signal, IOptions<CentralOptions> options, ILogger<BatchProcessor> log)
    : BackgroundService
{
    // Oldest unprocessed batch of any station that has no earlier unprocessed/failed batch.
    private const string NextBatchSql = """
        SELECT b.id, b.station_id AS StationId, b.payload::text AS Payload, b.attempts
        FROM raw_batches b
        WHERE b.status = 'received'
          AND NOT EXISTS (SELECT 1 FROM raw_batches p
                          WHERE p.station_id = b.station_id AND p.id < b.id AND p.status <> 'processed')
        ORDER BY b.id
        LIMIT 1
        FOR UPDATE SKIP LOCKED
        """;

    private sealed record PendingBatch(long Id, Guid StationId, string? Payload, int Attempts);

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
        var batch = await conn.QuerySingleOrDefaultAsync<PendingBatch>(new CommandDefinition(NextBatchSql, transaction: tx, cancellationToken: ct));
        if (batch is null) return false;

        var sw = Stopwatch.StartNew();
        try
        {
            await ApplyAsync(conn, tx, batch, ct);
            await conn.ExecuteAsync("UPDATE raw_batches SET status = 'processed', processed_at = now(), error = NULL WHERE id = @Id", new { batch.Id }, tx);
            await tx.CommitAsync(ct);
            AppMetrics.ProcessingSeconds.Observe(sw.Elapsed.TotalSeconds);
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

    private static async Task ApplyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, PendingBatch batch, CancellationToken ct)
    {
        if (batch.Payload is null) throw new InvalidOperationException("Payload was purged before processing");
        var envelope = JsonSerializer.Deserialize<BatchEnvelope>(batch.Payload, Protocol.Json)
                       ?? throw new InvalidOperationException("Empty payload");

        foreach (var item in envelope.Items)
        {
            Projections.ByDataset.TryGetValue(item.Dataset, out var projection);
            if (item.Op == Protocol.OpUpsert)
            {
                var data = item.Data!.ToJsonString();
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

                await InsertVersion(conn, tx, batch, item, version.Value, data, ct);
                if (projection is not null)
                    await conn.ExecuteAsync(new CommandDefinition(projection.UpsertSql, new { station = batch.StationId, data }, tx, cancellationToken: ct));
                AppMetrics.RecordsApplied.WithLabels("upsert").Inc();
            }
            else
            {
                var deleted = await conn.QuerySingleOrDefaultAsync<DeletedRow>(new CommandDefinition("""
                    UPDATE records SET deleted = true, version = version + 1, updated_at = now(), last_batch_id = @batch
                    WHERE station_id = @station AND dataset = @dataset AND key = @key AND NOT deleted
                    RETURNING version, data::text AS data
                    """, new { station = batch.StationId, dataset = item.Dataset, key = item.Key, batch = batch.Id }, tx, cancellationToken: ct));
                if (deleted is null) continue;

                await InsertVersion(conn, tx, batch, item, deleted.Version, null, ct);
                if (projection is not null && deleted.Data is not null)
                    await conn.ExecuteAsync(new CommandDefinition(projection.DeleteSql, new { station = batch.StationId, data = deleted.Data }, tx, cancellationToken: ct));
                AppMetrics.RecordsApplied.WithLabels("delete").Inc();
            }
        }
    }

    private sealed record DeletedRow(int Version, string? Data);

    private static Task InsertVersion(NpgsqlConnection conn, NpgsqlTransaction tx, PendingBatch batch, RecordChange item, int version, string? data, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO record_versions (station_id, dataset, key, version, op, hash, data, raw_batch_id)
            VALUES (@station, @dataset, @key, @version, @op, @hash, @data::jsonb, @batch)
            """, new { station = batch.StationId, dataset = item.Dataset, key = item.Key, version, op = item.Op, hash = item.Hash, data, batch = batch.Id },
            tx, cancellationToken: ct));
}
