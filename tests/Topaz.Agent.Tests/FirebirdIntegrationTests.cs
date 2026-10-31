using FirebirdSql.Data.FirebirdClient;
using Topaz.Agent.Configuration;
using Topaz.Agent.Source;
using Xunit;

namespace Topaz.Agent.Tests;

/// <summary>Runs only when a Firebird 2.5 server is available (CI service / local Docker).</summary>
public sealed class FirebirdFactAttribute : FactAttribute
{
    public FirebirdFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TOPAZ_TEST_FB_HOST")))
            Skip = "Set TOPAZ_TEST_FB_HOST (and TOPAZ_TEST_FB_PASSWORD) to run Firebird integration tests";
    }
}

/// <summary>
/// Creates a throw-away database with a Topaz-like subset of tables (quoted mixed-case names,
/// Topaz domains) on a real Firebird 2.5 server and reads it through the production code path.
/// </summary>
public sealed class FirebirdIntegrationTests : IAsyncLifetime
{
    private readonly AgentOptions _options = new();
    private string _connectionString = "";

    public async Task InitializeAsync()
    {
        var host = Environment.GetEnvironmentVariable("TOPAZ_TEST_FB_HOST");
        if (string.IsNullOrEmpty(host)) return;
        _options.Station.TimeZone = "Europe/Moscow";
        _options.Firebird = new FirebirdOptions
        {
            DataSource = host,
            Database = $"/firebird/data/test-{Guid.NewGuid():N}.fdb",
            Password = Environment.GetEnvironmentVariable("TOPAZ_TEST_FB_PASSWORD") ?? "masterkey",
        };
        _connectionString = _options.Firebird.BuildConnectionString();
        FbConnection.CreateDatabase(_connectionString, pageSize: 8192, forcedWrites: false);
        await using var c = new FbConnection(_connectionString);
        await c.OpenAsync();
        foreach (var sql in new[]
        {
            "CREATE DOMAIN TID AS INTEGER NOT NULL",
            "CREATE DOMAIN \"TCurrency\" AS NUMERIC(15, 2) DEFAULT 0",
            "CREATE TABLE \"sysSessions\" (\"SessionID\" TID, \"SessionNum\" INTEGER, \"UserName\" VARCHAR(50), \"StartDateTime\" TIMESTAMP, \"EndDateTime\" TIMESTAMP, PRIMARY KEY (\"SessionID\"))",
            "CREATE TABLE \"flOrders\" (\"OrderID\" TID, \"SessionID\" TID, \"Volume\" \"TCurrency\", \"DateTime\" TIMESTAMP, \"Remark\" CHAR(10), \"ExtData\" BLOB SUB_TYPE TEXT, PRIMARY KEY (\"OrderID\"))",
            "CREATE TABLE \"dcAgents\" (\"AgentID\" TID, \"Name\" VARCHAR(50), \"Password\" VARCHAR(50), PRIMARY KEY (\"AgentID\"))",
            "CREATE TABLE \"sysEvents\" (\"EventID\" TID, \"DateTime\" TIMESTAMP, \"EventString\" VARCHAR(1001), PRIMARY KEY (\"EventID\"))",
        })
        {
            await using var cmd = new FbCommand(sql, c);
            await cmd.ExecuteNonQueryAsync();
        }
        foreach (var sql in new[]
        {
            "INSERT INTO \"sysSessions\" VALUES (1, 1, 'Оператор', '2026-10-30 08:00:00', '2026-10-30 20:00:00')",
            "INSERT INTO \"sysSessions\" VALUES (2, 2, 'Оператор', '2026-10-30 20:00:00', NULL)",
            "INSERT INTO \"flOrders\" VALUES (10, 1, 23.03, '2026-10-30 09:15:00.5', 'СУГ', '<x/>')",
            "INSERT INTO \"flOrders\" VALUES (11, 2, 42.62, '2026-10-31 21:59:52.972', NULL, NULL)",
            "INSERT INTO \"dcAgents\" VALUES (1, 'Клиент', 'secret')",
            "INSERT INTO \"sysEvents\" VALUES (5, '2026-10-31 10:00:00', 'Смена открыта')",
            "INSERT INTO \"sysEvents\" VALUES (6, '2026-10-31 10:01:00', 'Отпуск')",
        })
        {
            await using var cmd = new FbCommand(sql, c);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public Task DisposeAsync()
    {
        if (_connectionString.Length > 0)
        {
            FbConnection.ClearAllPools();
            FbConnection.DropDatabase(_connectionString);
        }
        return Task.CompletedTask;
    }

    [FirebirdFact]
    public async Task Reads_session_snapshot_and_incremental_datasets()
    {
        await using var db = await new FirebirdTopazSource(_options).OpenAsync(default);
        Assert.StartsWith("2.5", await db.GetServerVersionAsync(default));

        var cols = await db.GetColumnsAsync(default);
        Assert.Contains("OrderID", cols["flOrders"]);

        var window = await db.GetSessionWindowAsync(0, 3, 10, default);
        Assert.Equal([1L, 2L], window.SessionIds);
        Assert.True(window.LatestOpen);

        var orders = await db.ReadSessionsAsync(new DatasetDefinition { Table = "flOrders", Kind = DatasetKind.Session, Key = ["OrderID"] }, [1, 2], default);
        var o10 = orders.Single(o => o.Key == "10");
        Assert.Equal(1, o10.Scope);
        Assert.Equal("2026-10-30T09:15:00.5000+03:00", o10.Data["DateTime"]!.GetValue<string>());
        Assert.Equal("СУГ", o10.Data["Remark"]!.GetValue<string>()); // CHAR padding trimmed
        Assert.Equal("<x/>", o10.Data["ExtData"]!.GetValue<string>());
        Assert.Equal("23.03", o10.Data["Volume"]!.ToJsonString());
        Assert.False(o10.Data.ContainsKey(SqlBuilder.ScopeAlias));

        var agents = await db.ReadSnapshotAsync(new DatasetDefinition { Table = "dcAgents", Kind = DatasetKind.Snapshot, Key = ["AgentID"], Exclude = ["Password"] }, default);
        Assert.False(agents.Single().Data.ContainsKey("Password"));

        var events = await db.ReadIncrementalAsync(new DatasetDefinition { Table = "sysEvents", Kind = DatasetKind.Incremental, Key = ["EventID"] }, 5, 100, default);
        Assert.Equal("6", Assert.Single(events).Key);
    }

    [FirebirdFact]
    public async Task Hash_of_unchanged_row_is_stable_across_reads()
    {
        var def = new DatasetDefinition { Table = "flOrders", Kind = DatasetKind.Session, Key = ["OrderID"] };
        await using var a = await new FirebirdTopazSource(_options).OpenAsync(default);
        var first = (await a.ReadSessionsAsync(def, [1, 2], default)).ToDictionary(r => r.Key, r => r.Hash);
        await using var b = await new FirebirdTopazSource(_options).OpenAsync(default);
        var second = (await b.ReadSessionsAsync(def, [1, 2], default)).ToDictionary(r => r.Key, r => r.Hash);
        Assert.Equal(first, second);
    }

    [FirebirdFact]
    public async Task Agent_transaction_cannot_modify_topaz()
    {
        // Even if a write statement slipped through, the read-only transaction makes the engine refuse it.
        await using var c = new FbConnection(_connectionString);
        await c.OpenAsync();
        await using var tx = await c.BeginTransactionAsync(new FbTransactionOptions
        {
            TransactionBehavior = FbTransactionBehavior.Read | FbTransactionBehavior.Concurrency | FbTransactionBehavior.NoWait,
        });
        await using var cmd = new FbCommand("UPDATE \"flOrders\" SET \"Volume\" = 0", c, tx);
        await Assert.ThrowsAsync<FbException>(() => cmd.ExecuteNonQueryAsync());
    }
}
