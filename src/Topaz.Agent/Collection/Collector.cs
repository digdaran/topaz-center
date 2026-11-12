using System.Globalization;
using Microsoft.Extensions.Logging;
using Topaz.Agent.Configuration;
using Topaz.Agent.Hosting;
using Topaz.Agent.Source;
using Topaz.Agent.State;
using Topaz.Contracts;

namespace Topaz.Agent.Collection;

/// <summary>Failures: datasets that could not be read this cycle ("name: error"); the others were collected.</summary>
public sealed record CycleResult(int DatasetsRead, int Changes, int Batches, int Skipped, IReadOnlyList<string>? Failures = null);

/// <summary>
/// One collection cycle: open a read-only snapshot of Topaz, read each dataset, diff rows against the
/// last-sent hashes and queue only what changed. Topaz has no modification timestamps on transactional
/// tables, so change detection is by row hash:
///   snapshot    - whole table; missing rows become deletes;
///   session     - rows of the current shift window; missing rows (within the window) become deletes;
///   incremental - id &gt; watermark - overlap; append-only.
/// Rows are streamed and handled in portions (Collection:ChunkRows): each portion is diffed against the
/// stored hashes of its keys and queued together with its row_state update in one SQLite transaction, so
/// memory does not depend on the table size. Deletes are found only after a dataset was read to the end.
/// </summary>
public sealed class Collector(
    ITopazSource source,
    StateStore state,
    DatasetCatalog catalog,
    AgentOptions options,
    AgentStatus status,
    TimeProvider clock,
    ILogger<Collector> log)
{
    internal const string SessionCursorKey = "session_cursor";
    internal const string LastFullRescanKey = "last_full_rescan";
    internal static string WatermarkKey(string dataset) => $"wm:{dataset}";

    private readonly Dictionary<string, DateTimeOffset> _lastSnapshot = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, HashSet<string>>? _columns;
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    public async Task<CycleResult> RunCycleAsync(CancellationToken ct)
    {
        var o = options.Collection;
        var now = clock.GetUtcNow();
        var version = AgentInfo.Version;

        await using var db = await source.OpenAsync(ct);
        status.TopazDbOk = true;
        status.TopazDbVersion ??= await db.GetServerVersionAsync(ct);
        _columns ??= await db.GetColumnsAsync(ct);

        // Periodic full rescan: walk all shifts again from the start (hash diff keeps traffic minimal).
        var lastRescan = state.GetKv(LastFullRescanKey);
        if (lastRescan is null || now - DateTimeOffset.Parse(lastRescan, CultureInfo.InvariantCulture) > TimeSpan.FromHours(o.FullRescanHours))
        {
            state.SetKv(SessionCursorKey, "0");
            state.SetKv(LastFullRescanKey, now.ToString("O", CultureInfo.InvariantCulture));
            log.LogInformation("Starting full shift rescan");
        }

        var cursor = state.GetLong(SessionCursorKey);
        var window = await db.GetSessionWindowAsync(cursor, o.ClosedSessionsWindow, o.BackfillSessionsPerCycle, ct);
        status.LastSessionId = window.LatestSessionId;
        status.LastSessionOpen = window.LatestOpen;

        var t = new Totals();
        var enabled = catalog.Datasets.Where(d => d.Enabled).ToList();

        // 1. Shifts. An error here stops the cycle before the backfill cursor moves, so no shift is skipped.
        foreach (var d in enabled.Where(d => d.Kind == DatasetKind.Session))
        {
            ct.ThrowIfCancellationRequested();
            if (!IsAvailable(d)) { t.Skipped++; continue; }
            if (window.SessionIds.Count == 0) continue;
            await PassAsync(d, db.ReadSessionsAsync(d, window.SessionIds, ct), window.SessionIds, deletes: true, t, version, now, ct);
        }
        // Shift backfill advances once every shift dataset of the chunk was committed — not held up by other tables.
        if (window.BackfillMax is { } max && max > cursor)
            state.SetKv(SessionCursorKey, max.ToString(CultureInfo.InvariantCulture));

        // 2. Logs and reference tables, each on its own: one unreadable table does not stop the others.
        var failures = new List<string>();
        var inARow = 0;
        foreach (var d in enabled.Where(d => d.Kind != DatasetKind.Session))
        {
            ct.ThrowIfCancellationRequested();
            if (!IsAvailable(d)) { t.Skipped++; continue; }
            try
            {
                if (d.Kind == DatasetKind.Snapshot)
                {
                    if (!DueSnapshot(d, now)) continue;
                    await PassAsync(d, db.ReadSnapshotAsync(d, ct), scopes: null, deletes: true, t, version, now, ct);
                    _lastSnapshot[d.DatasetName] = now;
                }
                else if (d.Kind == DatasetKind.Incremental)
                    await IncrementalAsync(db, d, t, version, now, ct);
                inARow = 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Dataset {Dataset} failed; the other datasets go on", d.DatasetName);
                failures.Add($"{d.DatasetName}: {ex.GetBaseException().Message}");
                // Several failures in a row: the Topaz connection itself is gone, stop this cycle.
                if (++inARow >= 3) throw;
            }
        }

        return new CycleResult(t.Read, t.Changes, t.Batches, t.Skipped, failures);
    }

    private sealed class Totals
    {
        public int Read, Changes, Batches, Skipped;
    }

    /// <summary>
    /// One pass over a dataset: portions of rows are diffed against their stored hashes and committed with their
    /// row_state updates; after the last row, rows that were sent before but not seen now become deletes
    /// (for shift datasets only within the given shifts). An interrupted pass sends no deletes.
    /// </summary>
    private async Task PassAsync(DatasetDefinition d, IAsyncEnumerable<SourceRow> rows, IReadOnlyCollection<long>? scopes, bool deletes,
                                 Totals t, string version, DateTimeOffset now, CancellationToken ct, Action<DatasetCommit>? last = null)
    {
        var size = options.Collection.ChunkRows;
        if (deletes) state.ClearSeen(d.DatasetName);
        var chunk = new List<SourceRow>(size);
        var changes = 0;
        await foreach (var row in rows.WithCancellation(ct))
        {
            chunk.Add(row);
            if (chunk.Count < size) continue;
            changes += CommitChunk(d, chunk, deletes, t, version, now, null);
            chunk.Clear();
        }
        changes += CommitChunk(d, chunk, deletes, t, version, now, last);

        if (deletes)
        {
            while (state.MissingKeys(d.DatasetName, scopes, size) is { Count: > 0 } missing)
            {
                ct.ThrowIfCancellationRequested();
                var commit = new DatasetCommit { Dataset = d.DatasetName };
                foreach (var (key, scope) in missing)
                {
                    commit.Changes.Add(new RecordChange
                    {
                        Dataset = d.DatasetName,
                        Op = Protocol.OpDelete,
                        Key = key,
                        SessionId = d.Kind == DatasetKind.Session ? scope : null,
                    });
                    commit.StateDeletes.Add(key);
                }
                t.Batches += state.Commit(commit, options.Collection.MaxItemsPerBatch, version, now);
                changes += commit.Changes.Count;
            }
            state.ClearSeen(d.DatasetName);
        }
        t.Read++;
        t.Changes += changes;
        if (changes > 0) log.LogInformation("{Dataset}: queued {Changes} change(s)", d.DatasetName, changes);
    }

    /// <summary>Diffs one portion and commits it (queue + row_state + seen keys) in one SQLite transaction.</summary>
    private int CommitChunk(DatasetDefinition d, List<SourceRow> chunk, bool deletes, Totals t, string version, DateTimeOffset now, Action<DatasetCommit>? last)
    {
        var commit = Diff(d, chunk, chunk.Count == 0 ? new Dictionary<string, RowStateEntry>() : state.LookupRowState(d.DatasetName, chunk.Select(r => r.Key).ToList()), deletes: false);
        if (deletes) commit.SeenKeys.AddRange(chunk.Select(r => r.Key));
        last?.Invoke(commit);
        if (commit.Changes.Count == 0 && commit.StateUpserts.Count == 0 && commit.SeenKeys.Count == 0 && commit.KvUpdates.Count == 0 && commit.PruneScopeBelow is null)
            return 0;
        t.Batches += state.Commit(commit, options.Collection.MaxItemsPerBatch, version, now);
        return commit.Changes.Count;
    }

    private bool DueSnapshot(DatasetDefinition d, DateTimeOffset now)
    {
        var interval = TimeSpan.FromSeconds(d.IntervalSeconds ?? options.Collection.SnapshotIntervalSeconds);
        return !_lastSnapshot.TryGetValue(d.DatasetName, out var last) || now - last >= interval;
    }

    private async Task IncrementalAsync(ITopazReadSession db, DatasetDefinition d, Totals t, string version, DateTimeOffset now, CancellationToken ct)
    {
        var wmKey = WatermarkKey(d.DatasetName);
        var watermark = state.GetLong(wmKey);
        var limit = options.Collection.IncrementalRowsPerCycle;
        // Overlap is capped so a cycle always makes progress past the watermark.
        var overlap = Math.Min(d.Overlap, limit / 2);
        var from = Math.Max(0, watermark - overlap);
        var newWatermark = watermark;
        // The watermark moves with the last portion only: an interrupted read is simply read again.
        await PassAsync(d, Track(db.ReadIncrementalAsync(d, from, limit, ct)), scopes: null, deletes: false, t, version, now, ct, last: commit =>
        {
            if (newWatermark == watermark) return;
            commit.KvUpdates[wmKey] = newWatermark.ToString(CultureInfo.InvariantCulture);
            commit.PruneScopeBelow = Math.Max(0, newWatermark - overlap);
        });

        async IAsyncEnumerable<SourceRow> Track(IAsyncEnumerable<SourceRow> rows)
        {
            await foreach (var r in rows.WithCancellation(ct))
            {
                if (r.Scope is { } s && s > newWatermark) newWatermark = s;
                yield return r;
            }
        }
    }

    /// <summary>Pure diff: new or changed rows become upserts, vanished rows (if enabled) deletes.</summary>
    internal static DatasetCommit Diff(DatasetDefinition d, IReadOnlyList<SourceRow> rows, IReadOnlyDictionary<string, RowStateEntry> known, bool deletes)
    {
        var commit = new DatasetCommit { Dataset = d.DatasetName };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!seen.Add(row.Key)) continue; // defensive: duplicate keys in the source
            if (known.TryGetValue(row.Key, out var prev) && prev.Hash == row.Hash && prev.Scope == row.Scope) continue;
            commit.Changes.Add(new RecordChange
            {
                Dataset = d.DatasetName,
                Op = Protocol.OpUpsert,
                Key = row.Key,
                Hash = row.Hash,
                SessionId = d.Kind == DatasetKind.Session ? row.Scope : null,
                Data = row.Data,
            });
            commit.StateUpserts[row.Key] = new RowStateEntry(row.Hash, row.Scope);
        }
        if (deletes)
        {
            foreach (var (key, prev) in known)
            {
                if (seen.Contains(key)) continue;
                commit.Changes.Add(new RecordChange
                {
                    Dataset = d.DatasetName,
                    Op = Protocol.OpDelete,
                    Key = key,
                    SessionId = d.Kind == DatasetKind.Session ? prev.Scope : null,
                });
                commit.StateDeletes.Add(key);
            }
        }
        return commit;
    }

    private bool IsAvailable(DatasetDefinition d)
    {
        if (_columns is null) return true;
        string? problem = null;
        if (!_columns.TryGetValue(d.Table, out var cols)) problem = $"table {d.Table} not found";
        else if (d.Key.FirstOrDefault(k => !cols.Contains(k)) is { } k) problem = $"key column {k} not found";
        else if (d.Kind == DatasetKind.Session && d.Parent is null && !cols.Contains(d.SessionColumn)) problem = $"column {d.SessionColumn} not found";
        else if (d.Parent is { } p && (!_columns.TryGetValue(p.Table, out var pc) || !pc.Contains(p.Key) || !pc.Contains(p.SessionColumn) || !cols.Contains(p.ForeignKey)))
            problem = $"parent link {p.Table} is invalid";
        if (problem is null) return true;
        if (_warned.Add(d.DatasetName))
            log.LogWarning("Dataset {Dataset} skipped: {Problem} (different Topaz version?)", d.DatasetName, problem);
        return false;
    }
}
