using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Topaz.Central.Processing;
using Topaz.Contracts;
using Xunit;

namespace Topaz.Central.Tests;

/// <summary>
/// 1.7.4, from Topaz's knowledge base: the database compaction must not erase history here, a payment mode with a
/// discounted namesake is reported, frequent overflows on a hose are reported.
/// </summary>
public sealed class TopazWikiRulesTests(CentralFixture fx) : IClassFixture<CentralFixture>
{
    private static string Hash(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    private static RecordChange Row(string dataset, string key, JsonObject data, long? session = null) =>
        new() { Dataset = dataset, Key = key, Hash = Hash(dataset + key + data.ToJsonString()), Data = data, SessionId = session };
    private static RecordChange Delete(string dataset, string key, long session) => new() { Dataset = dataset, Op = Protocol.OpDelete, Key = key, SessionId = session };
    private static string Ts(DateTimeOffset t) => t.ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffzzz");

    private static RecordChange Shift(int id, DateTimeOffset start, DateTimeOffset? end) => Row("sysSessions", $"{id}", new JsonObject
    {
        ["SessionID"] = id, ["SessionNum"] = id, ["UserName"] = "Оператор", ["StartDateTime"] = Ts(start),
        ["EndDateTime"] = end is null ? null : Ts(end.Value), ["Deleted"] = 0,
    }, id);

    private static RecordChange Order(int id, int session, DateTimeOffset at, int mode, decimal volume, decimal price, decimal pricePump, int hose = 1) =>
        Row("flOrders", $"{id}", new JsonObject
        {
            ["OrderID"] = id, ["SessionID"] = session, ["OrderNum"] = id, ["DateTime"] = Ts(at), ["FuelID"] = 1, ["TankID"] = 1, ["HoseID"] = hose,
            ["PaymentModeID"] = mode, ["Volume"] = volume, ["VolumeOrdered"] = volume, ["Price"] = price, ["PricePump"] = pricePump,
            ["Amount"] = Math.Round(volume * pricePump, 2), ["AmountCash"] = Math.Round(volume * price, 2), ["AmountCashless"] = 0, ["AmountDiscount"] = 0,
        }, session);

    private static RecordChange Mode(int id, string name) => Row("dcPaymentModes", $"{id}", new JsonObject { ["PaymentModeID"] = id, ["Name"] = name });

    private int _seq;

    private async Task SendAsync(string token, params RecordChange[] items)
    {
        var batch = new BatchEnvelope { BatchId = Guid.NewGuid(), Seq = ++_seq, CreatedAt = DateTimeOffset.UtcNow, AgentVersion = "t", Items = [.. items] };
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true)) JsonSerializer.Serialize(gz, batch, Protocol.Json);
        var content = new ByteArrayContent(ms.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        (await fx.Client(token).PostAsync("/api/v1/batches", content)).EnsureSuccessStatusCode();
        await fx.WaitProcessedAsync();
    }

    private Task<int> RunAlertsAsync() => fx.Factory!.Services.GetRequiredService<AnomalyDetector>().RunAsync(CancellationToken.None);

    [PostgresFact]
    public async Task Compaction_of_old_shifts_keeps_the_history_and_raises_an_alert()
    {
        var st = await fx.EnrollAsync("АЗС-свёртка");
        var now = DateTimeOffset.UtcNow;
        var oldStart = now.AddDays(-10);
        await SendAsync(st.Token,
            Mode(1, "НАЛИЧНЫЕ"),
            Shift(1, oldStart, oldStart.AddHours(12)), Shift(2, now.AddHours(-3), now.AddHours(-1)),
            Order(11, 1, oldStart.AddHours(1), 1, 10, 50, 50), Order(12, 1, oldStart.AddHours(2), 1, 20, 50, 50),
            Order(21, 2, now.AddHours(-2), 1, 30, 50, 50));
        Task<long> Count(string sql) => fx.QueryAsync<long>(sql, new { s = st.StationId });

        // The compaction removed the orders of shift 1 (10 days ago); an aborted order of today's shift 2 was removed too.
        await SendAsync(st.Token, Delete("flOrders", "11", 1), Delete("flOrders", "12", 1), Delete("flOrders", "21", 2));

        Assert.Equal(2, await Count("SELECT count(*) FROM fuel_sales WHERE station_id = @s AND session_id = 1"));
        Assert.Equal(0, await Count("SELECT count(*) FROM fuel_sales WHERE station_id = @s AND session_id = 2"));
        Assert.Equal(0, await Count("SELECT count(*) FROM records WHERE station_id = @s AND dataset = 'flOrders' AND key IN ('11', '12') AND deleted"));
        Assert.Equal(2, await Count("SELECT count(*) FROM held_deletes WHERE station_id = @s"));

        await RunAlertsAsync();
        Assert.Equal(2m, await fx.QueryAsync<decimal>("SELECT value FROM alerts WHERE station_id = @s AND rule_code = 'history_deleted'", new { s = st.StationId }));

        // The database is restored from a copy: the row comes back unchanged, its delete is no longer held.
        await SendAsync(st.Token, Order(11, 1, oldStart.AddHours(1), 1, 10, 50, 50));
        Assert.Equal(1, await Count("SELECT count(*) FROM held_deletes WHERE station_id = @s"));
        await RunAlertsAsync();
        Assert.Equal(1m, await fx.QueryAsync<decimal>("SELECT value FROM alerts WHERE station_id = @s AND rule_code = 'history_deleted'", new { s = st.StationId }));
        await SendAsync(st.Token, Order(12, 1, oldStart.AddHours(2), 1, 20, 50, 50));
        await RunAlertsAsync();
        Assert.Equal("closed", await fx.QueryAsync<string>("SELECT status FROM alerts WHERE station_id = @s AND rule_code = 'history_deleted'", new { s = st.StationId }));
    }

    [PostgresFact]
    public async Task Sales_at_a_payment_mode_whose_namesake_gives_the_discount_are_reported()
    {
        var st = await fx.EnrollAsync("АЗС-двойник");
        var t = DateTimeOffset.UtcNow.AddDays(-1);
        await SendAsync(st.Token,
            Mode(109, "НАЛИЧКА"), Mode(115, "НАЛИЧКА "), Mode(120, "Банковская карта"),
            Shift(1, t, t.AddHours(12)),
            Order(1, 1, t.AddHours(1), 115, 20, 48, 47),   // the new mode: 1 ₽ per litre off
            Order(2, 1, t.AddHours(2), 109, 30, 48, 48),   // the old mode: no discount
            Order(3, 1, t.AddHours(3), 109, 10, 48, 48),
            Order(4, 1, t.AddHours(4), 120, 15, 48, 48));  // another mode without a namesake: fine
        await RunAlertsAsync();

        var alert = await fx.QueryAsync<(string Key, decimal Value, string Details)>(
            "SELECT dedupe_key, value, details FROM alerts WHERE station_id = @s AND rule_code = 'payment_mode_twin'", new { s = st.StationId });
        Assert.Equal("1/109", alert.Key);
        Assert.Equal(2m, alert.Value);
        Assert.Contains("код 115", alert.Details);
    }

    [PostgresFact]
    public async Task Frequent_overflows_on_one_hose_are_reported()
    {
        var st = await fx.EnrollAsync("АЗС-переливы");
        var t = DateTimeOffset.UtcNow.AddDays(-2);
        var items = new List<RecordChange>
        {
            Mode(1, "НАЛИЧНЫЕ"), Mode(9, "Переливы"),
            Row("dcPumps", "1", new JsonObject { ["PumpID"] = 1, ["Num"] = 3, ["Deleted"] = 0 }),
            Row("dcHoses", "1", new JsonObject { ["HoseID"] = 1, ["PumpID"] = 1, ["TankID"] = 1, ["Num"] = 1, ["NumInPump"] = 2, ["Deleted"] = 0 }),
            Row("dcHoses", "2", new JsonObject { ["HoseID"] = 2, ["PumpID"] = 1, ["TankID"] = 1, ["Num"] = 2, ["NumInPump"] = 1, ["Deleted"] = 0 }),
            Shift(1, t, t.AddHours(24)),
        };
        for (var i = 0; i < 6; i++) items.Add(Order(100 + i, 1, t.AddHours(i + 1), 9, i == 0 ? -0.5m : 0.2m, 50, 50, hose: 1));
        for (var i = 0; i < 4; i++) items.Add(Order(200 + i, 1, t.AddHours(i + 1), 9, 0.1m, 50, 50, hose: 2));
        await SendAsync(st.Token, [.. items]);
        await RunAlertsAsync();

        Assert.Equal(1, await fx.QueryAsync<long>("SELECT count(*) FROM alerts WHERE station_id = @s AND rule_code = 'hose_overflows'", new { s = st.StationId }));
        var alert = await fx.QueryAsync<(string Title, decimal Value, string Details)>(
            "SELECT title, value, details FROM alerts WHERE station_id = @s AND rule_code = 'hose_overflows'", new { s = st.StationId });
        Assert.Equal(6m, alert.Value);
        Assert.Contains("колонка 3, рукав 2", alert.Title);
        Assert.Contains("отрицательных: 1", alert.Details);
    }
}
