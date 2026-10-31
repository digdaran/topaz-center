using System.Text.Json.Nodes;
using Topaz.Agent.Configuration;
using Topaz.Agent.Source;
using Topaz.Agent.State;
using Xunit;

namespace Topaz.Agent.Tests;

public sealed class SerializationTests
{
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

    [Fact]
    public void Local_topaz_timestamp_gets_station_offset()
    {
        var v = RowSerializer.ToJsonValue(new DateTime(2026, 10, 31, 21, 59, 52, 972), "TIMESTAMP", Moscow);
        Assert.Equal("2026-10-31T21:59:52.9720+03:00", v!.GetValue<string>());
    }

    [Fact]
    public void Date_char_and_decimal_formats_are_stable()
    {
        Assert.Equal("2026-10-31", RowSerializer.ToJsonValue(new DateTime(2026, 10, 31), "DATE", Moscow)!.GetValue<string>());
        Assert.Equal("АИ", RowSerializer.ToJsonValue("АИ   ", "CHAR", Moscow)!.GetValue<string>());
        Assert.Equal("АИ   ", RowSerializer.ToJsonValue("АИ   ", "VARCHAR", Moscow)!.GetValue<string>());
        Assert.Equal("ab", RowSerializer.ToJsonValue("a\0b", "VARCHAR", Moscow)!.GetValue<string>());
        Assert.Equal("23.03", RowSerializer.ToJsonValue(23.03m, "NUMERIC", Moscow)!.ToJsonString());
        Assert.Null(RowSerializer.ToJsonValue(DBNull.Value, "INTEGER", Moscow));
    }

    [Fact]
    public void Hash_depends_on_content_only()
    {
        var a = new JsonObject { ["OrderID"] = 1, ["Name"] = "СУГ" }.ToJsonString(RowSerializer.CanonicalJson);
        var b = new JsonObject { ["OrderID"] = 1, ["Name"] = "СУГ" }.ToJsonString(RowSerializer.CanonicalJson);
        var c = new JsonObject { ["OrderID"] = 1, ["Name"] = "Аи92" }.ToJsonString(RowSerializer.CanonicalJson);
        Assert.Equal(RowSerializer.Hash(a), RowSerializer.Hash(b));
        Assert.NotEqual(RowSerializer.Hash(a), RowSerializer.Hash(c));
        Assert.Matches("^[0-9a-f]{64}$", RowSerializer.Hash(a));
        Assert.Contains("СУГ", a); // compact UTF-8, not \u escapes
    }

    [Fact]
    public void Composite_keys_are_joined()
    {
        Assert.Equal("82|233", RowSerializer.KeyOf([82, 233L]));
    }

    [Fact]
    public void Sql_is_select_only_with_quoted_identifiers()
    {
        var orders = new DatasetDefinition { Table = "flOrders", Kind = DatasetKind.Session, Key = ["OrderID"] };
        Assert.Equal("SELECT t.*, t.\"SessionID\" AS \"__scope\" FROM \"flOrders\" t WHERE t.\"SessionID\" IN (@s0, @s1)",
            SqlBuilder.Session(orders, 2));

        var lines = new DatasetDefinition
        {
            Table = "trFastTradeD", Kind = DatasetKind.Session, Key = ["FastTradeDID"],
            Parent = new ParentLink { Table = "trFastTradeH", Key = "FastTradeHID", ForeignKey = "FastTradeHID" },
        };
        Assert.Contains("JOIN \"trFastTradeH\" p ON p.\"FastTradeHID\" = t.\"FastTradeHID\"", SqlBuilder.Session(lines, 1));

        var events = new DatasetDefinition { Table = "sysEvents", Kind = DatasetKind.Incremental, Key = ["EventID"] };
        Assert.Equal("SELECT FIRST 500 t.*, t.\"EventID\" AS \"__scope\" FROM \"sysEvents\" t WHERE t.\"EventID\" > @from ORDER BY t.\"EventID\"",
            SqlBuilder.Incremental(events, 500));
    }

    [Theory]
    [InlineData("flOrders\"; DROP TABLE x; --")]
    [InlineData("a b")]
    [InlineData("")]
    public void Malicious_identifiers_are_rejected(string table)
    {
        Assert.Throws<ArgumentException>(() => SqlIdentifier.Quote(table));
        var catalog = new DatasetCatalog { Datasets = { new DatasetDefinition { Table = table, Key = ["ID"] } } };
        Assert.NotEmpty(catalog.Validate());
    }

    [Fact]
    public void Shipped_catalog_is_valid()
    {
        var catalog = DatasetCatalog.Load(Path.Combine(AppContext.BaseDirectory, "datasets.json"));
        Assert.True(catalog.Datasets.Count > 100);
        Assert.Contains(catalog.Datasets, d => d.Table == "flOrders" && d.Kind == DatasetKind.Session);
        Assert.Contains(catalog.Datasets, d => d.Table == "sysEvents" && d.Kind == DatasetKind.Incremental);
        // Secrets are never exported.
        Assert.Contains("Password", catalog.Datasets.Single(d => d.Table == "dcAgents").Exclude);
        Assert.Contains("PswHash", catalog.Datasets.Single(d => d.Table == "sysUsers").Exclude);
    }

    [Fact]
    public void Queue_and_state_survive_restart()
    {
        using var dir = new TempDir();
        using (var s1 = new StateStore(dir.Path))
        {
            var commit = new DatasetCommit { Dataset = "dcFuels" };
            commit.Changes.Add(new Contracts.RecordChange { Dataset = "dcFuels", Key = "1", Hash = new string('a', 64), Data = new JsonObject() });
            commit.StateUpserts["1"] = new RowStateEntry(new string('a', 64), null);
            commit.KvUpdates["wm:x"] = "42";
            s1.Commit(commit, 100, "t", DateTimeOffset.UtcNow);
        }
        using var s2 = new StateStore(dir.Path);
        Assert.Equal(1, s2.GetQueueStats().Pending);
        Assert.Equal(42, s2.GetLong("wm:x"));
        Assert.Single(s2.LoadRowState("dcFuels"));
    }
}
