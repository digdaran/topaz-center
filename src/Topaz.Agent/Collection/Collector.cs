using System.Globalization;
using Microsoft.Extensions.Logging;
using Topaz.Agent.Configuration;
using Topaz.Agent.Hosting;
using Topaz.Agent.Source;
using Topaz.Agent.State;
using Topaz.Contracts;

namespace Topaz.Agent.Collection;

public sealed record CycleResult(int DatasetsRead, int Changes, int Batches, int Skipped);

/// <summary>
/// One collection cycle: open a read-only snapshot of Topaz, read each dataset, diff rows against the
/// last-sent hashes and queue only what changed. Topaz has no modification timestamps on transactional
/// tables, so change detection is by row hash:
///   snapshot    - whole table; missing rows become deletes;
///   session     - rows of the current shift window; missing rows (within the window) become deletes;
///   incremental - id &gt; watermark - overlap; append-only.
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

        int read = 0, changes = 0, batches = 0, skipped = 0;
        foreach (var d in catalog.Datasets.Where(d => d.Enabled))
        {
            ct.ThrowIfCancellationRequested();
            if (!IsAvailable(d)) { skipped++; continue; }

            DatasetCommit? commit = d.Kind switch
            {
                DatasetKind.Snapshot => DueSnapshot(d, now) ? Diff(d, await db.ReadSnapshotAsync(d, ct), state.LoadRowState(d.DatasetName), deletes: true) : null,
                DatasetKind.Session => window.SessionIds.Count == 0 ? null
                    : Diff(d, await db.ReadSessionsAsync(d, window.SessionIds, ct), state.LoadRowStateForScopes(d.DatasetName, window.SessionIds), deletes: true),
                DatasetKind.Incremental => await Incremental(db, d, ct),
                _ => null,
            };
            if (commit is null) continue;
            read++;
            if (d.Kind == DatasetKind.Snapshot) _lastSnapshot[d.DatasetName] = now;
            if (commit.Changes.Count == 0 && commit.StateUpserts.Count == 0 && commit.KvUpdates.Count == 0 && commit.PruneScopeBelow is null)
                continue;

            batches += state.Commit(commit, o.MaxItemsPerBatch, version, now);
            changes += commit.Changes.Count;
            if (commit.Changes.Count > 0)
                log.LogInformation("{Dataset}: queued {Changes} change(s)", d.DatasetName, commit.Changes.Count);
        }

        // Shift backfill advances only after every session dataset of the chunk was committed.
        if (window.BackfillMax is { } max && max > cursor)
            state.SetKv(SessionCursorKey, max.ToString(CultureInfo.InvariantCulture));

        return new CycleResult(read, changes, batches, skipped);
    }

    private bool DueSnapshot(DatasetDefinition d, DateTimeOffset now)
    {
        var interval = TimeSpan.FromSeconds(d.IntervalSeconds ?? options.Collection.SnapshotIntervalSeconds);
        return !_lastSnapshot.TryGetValue(d.DatasetName, out var last) || now - last >= interval;
    }

    private async Task<DatasetCommit?> Incremental(ITopazReadSession db, DatasetDefinition d, CancellationToken ct)
    {
        var wmKey = WatermarkKey(d.DatasetName);
        var watermark = state.GetLong(wmKey);
        var limit = options.Collection.IncrementalRowsPerCycle;
        // Overlap is capped so a cycle always makes progress past the watermark.
        var overlap = Math.Min(d.Overlap, limit / 2);
        var from = Math.Max(0, watermark - overlap);
        var rows = await db.ReadIncrementalAsync(d, from, limit, ct);
        var commit = Diff(d, rows, state.LoadRowStateFromScope(d.DatasetName, from), deletes: false);

        var newWatermark = rows.Count == 0 ? watermark : Math.Max(watermark, rows.Max(r => r.Scope ?? 0));
        if (newWatermark != watermark)
        {
            commit.KvUpdates[wmKey] = newWatermark.ToString(CultureInfo.InvariantCulture);
            commit.PruneScopeBelow = Math.Max(0, newWatermark - overlap);
        }
        return commit;
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
