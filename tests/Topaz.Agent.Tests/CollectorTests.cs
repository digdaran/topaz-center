using Microsoft.Extensions.Logging.Abstractions;
using Topaz.Agent.Collection;
using Topaz.Agent.Configuration;
using Topaz.Agent.Hosting;
using Topaz.Agent.State;
using Topaz.Contracts;
using Xunit;

namespace Topaz.Agent.Tests;

public sealed class CollectorTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly StateStore _state;
    private readonly FakeTopaz _topaz = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    private readonly AgentOptions _options = new();
    private readonly DatasetCatalog _catalog = new()
    {
        Datasets =
        {
            new DatasetDefinition { Table = "dcFuels", Kind = DatasetKind.Snapshot, Key = ["FuelID"] },
            new DatasetDefinition { Table = "flOrders", Kind = DatasetKind.Session, Key = ["OrderID"] },
            new DatasetDefinition { Table = "sysEvents", Kind = DatasetKind.Incremental, Key = ["EventID"], Overlap = 10 },
        },
    };

    public CollectorTests()
    {
        _state = new StateStore(_dir.Path);
        _options.Collection.ClosedSessionsWindow = 1;
        _options.Collection.BackfillSessionsPerCycle = 2;
        _options.Collection.MaxItemsPerBatch = 3;
    }

    private Collector NewCollector() =>
        new(_topaz, _state, _catalog, _options, new AgentStatus(), _time, NullLogger<Collector>.Instance);

    private List<RecordChange> Drain()
    {
        var items = new List<RecordChange>();
        while (_state.PeekPending() is { } b)
        {
            items.AddRange(StateStore.Gunzip(b.Payload).Items);
            _state.Ack(b.Seq);
        }
        return items;
    }

    [Fact]
    public async Task First_cycle_sends_everything_second_cycle_sends_nothing()
    {
        _topaz.Sessions[1] = false;
        _topaz.Tables["dcFuels"] = [FakeTopaz.Row("186", null, new { FuelID = 186, Name = "Аи92" })];
        _topaz.Tables["flOrders"] = [FakeTopaz.Row("10", 1, new { OrderID = 10, SessionID = 1, Volume = 20.5m })];
        _topaz.Tables["sysEvents"] = [FakeTopaz.Row("100", 100, new { EventID = 100 })];
        var c = NewCollector();

        await c.RunCycleAsync(default);
        var first = Drain();
        Assert.Equal(3, first.Count);
        Assert.All(first, i => Assert.Equal(Protocol.OpUpsert, i.Op));
        Assert.Equal(1, first.Single(i => i.Dataset == "flOrders").SessionId);

        _time.Advance(TimeSpan.FromHours(1)); // snapshot due again, still unchanged
        await c.RunCycleAsync(default);
        Assert.Empty(Drain());
    }

    [Fact]
    public async Task Changed_row_is_resent_and_vanished_row_becomes_delete()
    {
        _topaz.Sessions[1] = true;
        _topaz.Tables["flOrders"] =
        [
            FakeTopaz.Row("10", 1, new { OrderID = 10, Volume = 0m }),
            FakeTopaz.Row("11", 1, new { OrderID = 11, Volume = 5m }),
        ];
        var c = NewCollector();
        await c.RunCycleAsync(default);
        Drain();

        // Order 10 is updated after fuelling ends (Topaz inserts with 0 and updates later); order 11 removed.
        _topaz.Tables["flOrders"] = [FakeTopaz.Row("10", 1, new { OrderID = 10, Volume = 23.03m })];
        await c.RunCycleAsync(default);
        var items = Drain().Where(i => i.Dataset == "flOrders").ToList();

        Assert.Equal(2, items.Count);
        Assert.Equal("23.03", items.Single(i => i.Key == "10").Data!["Volume"]!.ToJsonString());
        var del = items.Single(i => i.Key == "11");
        Assert.Equal(Protocol.OpDelete, del.Op);
        Assert.Equal(1, del.SessionId);
    }

    [Fact]
    public async Task Rows_of_shifts_outside_the_window_are_not_deleted()
    {
        _topaz.Sessions[1] = false;
        _topaz.Sessions[2] = false;
        _topaz.Sessions[3] = true;
        _topaz.Tables["flOrders"] = [FakeTopaz.Row("1", 1, new { OrderID = 1 }), FakeTopaz.Row("3", 3, new { OrderID = 3 })];
        _options.Collection.BackfillSessionsPerCycle = 10;
        var c = NewCollector();
        await c.RunCycleAsync(default);
        Assert.Equal(2, Drain().Count);

        // Next cycle: backfill done; window = open(3) + last closed(2). Session 1 is outside and must not look deleted.
        await c.RunCycleAsync(default);
        Assert.Empty(Drain());
        Assert.DoesNotContain(1L, _topaz.SessionReads.Last());
    }

    [Fact]
    public async Task Backfill_walks_old_shifts_in_chunks()
    {
        for (var i = 1; i <= 7; i++) _topaz.Sessions[i] = false;
        _topaz.Tables["flOrders"] = Enumerable.Range(1, 7).Select(i => FakeTopaz.Row($"{i}", i, new { OrderID = i })).ToList();
        var c = NewCollector();

        var sent = new HashSet<string>();
        for (var cycle = 0; cycle < 5; cycle++)
        {
            await c.RunCycleAsync(default);
            foreach (var i in Drain()) sent.Add(i.Key);
        }
        Assert.Equal(7, sent.Count);
        Assert.Equal("7", _state.GetKv(Collector.SessionCursorKey));
    }

    [Fact]
    public async Task Incremental_advances_watermark_and_catches_late_commits_within_overlap()
    {
        _topaz.Tables["sysEvents"] = [FakeTopaz.Row("100", 100, new { EventID = 100 }), FakeTopaz.Row("102", 102, new { EventID = 102 })];
        var c = NewCollector();
        await c.RunCycleAsync(default);
        Assert.Equal(2, Drain().Count);
        Assert.Equal("102", _state.GetKv(Collector.WatermarkKey("sysEvents")));

        // EventID 101 was in an older transaction that committed later than 102.
        _topaz.Tables["sysEvents"].Add(FakeTopaz.Row("101", 101, new { EventID = 101 }));
        await c.RunCycleAsync(default);
        var items = Drain();
        Assert.Equal("101", Assert.Single(items).Key);
    }

    [Fact]
    public async Task Changes_are_split_into_batches_of_max_size_in_order()
    {
        _topaz.Tables["dcFuels"] = Enumerable.Range(1, 7).Select(i => FakeTopaz.Row($"{i}", null, new { FuelID = i })).ToList();
        await NewCollector().RunCycleAsync(default);

        var seqs = new List<long>();
        while (_state.PeekPending() is { } b)
        {
            seqs.Add(b.Seq);
            Assert.True(b.ItemCount <= 3);
            var env = StateStore.Gunzip(b.Payload);
            Assert.Equal(b.Seq, env.Seq);
            Assert.Equal(b.BatchId, env.BatchId);
            Assert.Empty(BatchValidator.Validate(env));
            _state.Ack(b.Seq);
        }
        Assert.Equal(3, seqs.Count);
        Assert.Equal(seqs.Order(), seqs);
    }

    [Fact]
    public async Task Missing_tables_are_skipped_not_fatal()
    {
        _catalog.Datasets.Add(new DatasetDefinition { Table = "flNotInThisVersion", Kind = DatasetKind.Snapshot, Key = ["ID"] });
        _topaz.Tables["dcFuels"] = [FakeTopaz.Row("1", null, new { FuelID = 1 })];
        var result = await NewCollector().RunCycleAsync(default);
        Assert.True(result.Skipped >= 1);
        Assert.Single(Drain());
    }

    [Fact]
    public async Task A_big_table_goes_in_portions_and_deletes_come_after_a_full_read()
    {
        _options.Collection.ChunkRows = 100;
        _options.Collection.MaxItemsPerBatch = 500;
        _topaz.Tables["dcFuels"] = Enumerable.Range(1, 5000).Select(i => FakeTopaz.Row($"{i}", null, new { FuelID = i, Name = "Т" + i })).ToList();
        var c = NewCollector();
        await c.RunCycleAsync(default);
        var first = Drain();
        Assert.Equal(5000, first.Count);
        Assert.All(first, i => Assert.Equal(Protocol.OpUpsert, i.Op));
        Assert.Equal(5000, _state.LoadRowState("dcFuels").Count);

        // Unchanged: nothing goes out.
        _time.Advance(TimeSpan.FromHours(1));
        await c.RunCycleAsync(default);
        Assert.Empty(Drain());

        // One row vanished and the read breaks in the middle: no delete is sent from a half-read table.
        _topaz.Tables["dcFuels"].RemoveAt(4321);
        _topaz.FailAfter["dcFuels"] = 2500;
        _time.Advance(TimeSpan.FromHours(1));
        var broken = await c.RunCycleAsync(default);
        Assert.Contains(broken.Failures!, f => f.StartsWith("dcFuels:", StringComparison.Ordinal));
        Assert.Empty(Drain());

        // The next full read sends exactly that delete.
        _topaz.FailAfter.Clear();
        _time.Advance(TimeSpan.FromHours(1));
        var ok = await c.RunCycleAsync(default);
        Assert.Empty(ok.Failures!);
        var del = Assert.Single(Drain());
        Assert.Equal(Protocol.OpDelete, del.Op);
        Assert.Equal("4322", del.Key);
    }

    [Fact]
    public async Task An_unreadable_table_does_not_stop_shifts_or_other_tables()
    {
        _catalog.Datasets.Insert(0, new DatasetDefinition { Table = "ipBroken", Kind = DatasetKind.Snapshot, Key = ["ID"] });
        _topaz.Tables["ipBroken"] = [FakeTopaz.Row("1", null, new { ID = 1 })];
        _topaz.FailAfter["ipBroken"] = 0;
        for (var i = 1; i <= 4; i++) _topaz.Sessions[i] = false;
        _topaz.Tables["flOrders"] = Enumerable.Range(1, 4).Select(i => FakeTopaz.Row($"{i}", i, new { OrderID = i })).ToList();
        _topaz.Tables["dcFuels"] = [FakeTopaz.Row("186", null, new { FuelID = 186 })];
        _topaz.Tables["sysEvents"] = [FakeTopaz.Row("100", 100, new { EventID = 100 })];

        var result = await NewCollector().RunCycleAsync(default);
        Assert.Equal("ipBroken: read of ipBroken broke", Assert.Single(result.Failures!));
        var sent = Drain();
        Assert.Contains(sent, i => i.Dataset == "dcFuels");
        Assert.Contains(sent, i => i.Dataset == "sysEvents");
        Assert.Contains(sent, i => i.Dataset == "flOrders");
        Assert.Equal("2", _state.GetKv(Collector.SessionCursorKey)); // backfill moved on
    }

    [Fact]
    public async Task Incremental_watermark_moves_only_with_the_last_portion()
    {
        _options.Collection.ChunkRows = 10;
        _options.Collection.MaxItemsPerBatch = 100;
        _topaz.Tables["sysEvents"] = Enumerable.Range(1, 35).Select(i => FakeTopaz.Row($"{i}", i, new { EventID = i })).ToList();
        _topaz.FailAfter["sysEvents"] = 25;
        var c = NewCollector();
        var broken = await c.RunCycleAsync(default);
        Assert.Single(broken.Failures!);
        Assert.Null(_state.GetKv(Collector.WatermarkKey("sysEvents")));
        Assert.Equal(20, Drain().Count); // two full portions were already safe to send

        _topaz.FailAfter.Clear();
        await c.RunCycleAsync(default);
        Assert.Equal(15, Drain().Count); // the rest; the first 20 are not sent twice
        Assert.Equal("35", _state.GetKv(Collector.WatermarkKey("sysEvents")));
    }

    public void Dispose()
    {
        _state.Dispose();
        _dir.Dispose();
    }
}
