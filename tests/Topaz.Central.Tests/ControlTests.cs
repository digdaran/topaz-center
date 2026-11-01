using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Topaz.Central.Ui;
using Topaz.Contracts;
using Xunit;

namespace Topaz.Central.Tests;

public sealed class ControlTests(CentralFixture fx) : IClassFixture<CentralFixture>
{
    private static string Hash(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    private static RecordChange Row(string dataset, string key, JsonObject data) => new() { Dataset = dataset, Key = key, Hash = Hash(dataset + key + data.ToJsonString()), Data = data };

    /// <summary>Captures what would go to the Telegram Bot API.</summary>
    internal sealed class FakeTelegram : HttpMessageHandler
    {
        public List<(string Url, string Body)> Sent { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // Store chat id + decoded text (JSON escapes Cyrillic).
            var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            Sent.Add((request.RequestUri!.ToString(), $"{json["chat_id"]}\n{json["text"]}"));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") };
        }
    }

    private static string Ts(DateTimeOffset t) => t.ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffzzz");

    /// <summary>
    /// Two closed shifts (yesterday and today) with planted problems: the hose counter jumps 5 l between shifts,
    /// shift 2 dispenses 40 l by orders while its counter moved 45 l, 6 of 7 orders in shift 2 are aborted,
    /// a delivery is 20 l (2 %) short, and the tank holds 1 000 l of 10 000 l while selling ~966 l a day.
    /// </summary>
    private async Task<Guid> StationAsync(string name)
    {
        var st = await fx.EnrollAsync(name);
        var now = DateTimeOffset.UtcNow;
        var s1 = now.AddHours(-30);
        var s2 = now.AddHours(-6);
        var items = new List<RecordChange>
        {
            Row("dcFuels", "1", new JsonObject { ["FuelID"] = 1, ["Name"] = "АИ-92", ["Deleted"] = 0 }),
            Row("dcTanks", "1", new JsonObject { ["TankID"] = 1, ["Num"] = 1, ["FuelID"] = 1, ["MaxVolume"] = 10000, ["DeadRestLiter"] = 300, ["Deleted"] = 0 }),
            Row("sysSessions", "1", new JsonObject { ["SessionID"] = 1, ["SessionNum"] = 1, ["UserName"] = "Петров", ["StartDateTime"] = Ts(s1), ["EndDateTime"] = Ts(s1.AddHours(23)), ["Deleted"] = 0 }),
            Row("sysSessions", "2", new JsonObject { ["SessionID"] = 2, ["SessionNum"] = 2, ["UserName"] = "Сидоров", ["StartDateTime"] = Ts(s2), ["EndDateTime"] = Ts(s2.AddHours(5)), ["Deleted"] = 0 }),
            Row("flSesHoses", "1|1", new JsonObject { ["SessionID"] = 1, ["HoseID"] = 1, ["StartAdder"] = 1000, ["EndAdder"] = 2000, ["PumpNum"] = 1, ["NumInPump"] = 1, ["TankNum"] = 1, ["FuelName"] = "АИ-92" }),
            Row("flSesHoses", "2|1", new JsonObject { ["SessionID"] = 2, ["HoseID"] = 1, ["StartAdder"] = 2005, ["EndAdder"] = 2050, ["PumpNum"] = 1, ["NumInPump"] = 1, ["TankNum"] = 1, ["FuelName"] = "АИ-92" }),
            Row("flSesTanks", "2|1", new JsonObject { ["SessionID"] = 2, ["TankID"] = 1, ["TankNum"] = 1, ["FuelName"] = "АИ-92", ["StartFuelVolume"] = 1040, ["ReceptVolume"] = 0, ["PumpVolume"] = 40 }),
            Row("flRecepts", "1", new JsonObject { ["ReceptID"] = 1, ["SessionID"] = 2, ["ReceptDateTime"] = Ts(s2.AddHours(1)), ["TankID"] = 1, ["FuelID"] = 1, ["DocNumber"] = "ТТН-1", ["DocCarNumber"] = "А001АА", ["DocVolume"] = 1000, ["MeasuredVolume"] = 980, ["Discrepancy"] = -20 }),
        };
        var id = 0;
        void Order(int session, DateTimeOffset at, decimal volume) => items.Add(Row("flOrders", $"{++id}", new JsonObject
        {
            ["OrderID"] = id, ["SessionID"] = session, ["OrderNum"] = id, ["DateTime"] = Ts(at), ["FuelID"] = 1, ["TankID"] = 1, ["HoseID"] = 1, ["PaymentModeID"] = 1,
            ["Remark"] = "", ["Price"] = 50m, ["PricePump"] = 50m, ["Volume"] = volume, ["Amount"] = volume * 50, ["AmountCash"] = volume * 50, ["AmountCashless"] = 0, ["AmountDiscount"] = 0,
        }));
        for (var i = 0; i < 10; i++) Order(1, s1.AddHours(1 + i), 100m);        // 1000 l, matches the counter
        Order(2, s2.AddHours(1), 40m);                                          // 40 l by orders, counter 45 l
        for (var i = 0; i < 6; i++) Order(2, s2.AddHours(2).AddMinutes(i), 0m); // aborted
        // Outflow history outside these shifts: 26 days × 1 000 l (session 0 = not in the checked shifts)
        for (var d = 2; d <= 27; d++) Order(0, now.AddDays(-d), 1000m);

        var batch = new BatchEnvelope { BatchId = Guid.NewGuid(), Seq = 1, CreatedAt = DateTimeOffset.UtcNow, AgentVersion = "t", Items = items };
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true)) JsonSerializer.Serialize(gz, batch, Protocol.Json);
        var content = new ByteArrayContent(ms.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        (await fx.Client(st.Token).PostAsync("/api/v1/batches", content)).EnsureSuccessStatusCode();
        await fx.WaitProcessedAsync();
        return st.StationId;
    }

    [PostgresFact]
    public async Task Rules_find_planted_problems_once_and_telegram_gets_them()
    {
        var telegram = new FakeTelegram();
        await using var app = fx.Factory!.WithWebHostBuilder(b =>
        {
            b.UseSetting("Topaz:TelegramBotToken", "123:secret");
            b.UseSetting("Topaz:TelegramApiBase", "http://telegram.test");
            b.ConfigureTestServices(s => s.AddHttpClient("telegram").ConfigurePrimaryHttpMessageHandler(() => telegram));
        });
        var station = await StationAsync("АЗС-control");
        var users = app.Services.GetRequiredService<UserService>();
        try { await users.CreateAsync("control-owner", "o", Roles.Owner, "control-password-1"); } catch (Npgsql.PostgresException) { }
        var owner = app.CreateClient();
        owner.DefaultRequestHeaders.Add(UiAuthSetup.CsrfHeader, "1");
        (await owner.PostAsJsonAsync("/api/v1/ui/auth/login", new { login = "control-owner", password = "control-password-1" })).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync("/api/v1/ui/notifications", new { target = "-100500", title = "Дежурные", min_severity = "warning", daily_digest = false })).EnsureSuccessStatusCode();

        var run = await owner.PostAsync("/api/v1/ui/alerts/run", null);
        run.EnsureSuccessStatusCode();
        var today = DateTime.UtcNow.AddHours(3).ToString("yyyy-MM-dd");
        var list = await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/alerts?stations={station}&from={DateTime.UtcNow.AddDays(-40):yyyy-MM-dd}&to={today}");
        var alerts = list!["alerts"]!.AsArray().Select(a => a!).ToList();
        string[] Codes() => alerts.Select(a => a["rule_code"]!.GetValue<string>()).ToArray();

        var gap = alerts.Single(a => a["rule_code"]!.GetValue<string>() == "counter_gap");
        Assert.Equal(5m, gap["value"]!.GetValue<decimal>());
        Assert.Equal(2, gap["session_id"]!.GetValue<long>());
        Assert.Equal(5m, alerts.Single(a => a["rule_code"]!.GetValue<string>() == "counter_mismatch")["value"]!.GetValue<decimal>());
        Assert.Contains("aborted_share", Codes());
        Assert.Equal(20m, alerts.Single(a => a["rule_code"]!.GetValue<string>() == "receipt_discrepancy")["value"]!.GetValue<decimal>());
        Assert.Contains("low_fuel", Codes()); // 1 000 l − 300 l dead rest at 1 000 l/day < 2 + 1 days

        // Telegram: one grouped message with the fresh (last 24 h) critical and warning alerts; the token stays in the URL only.
        var msg = Assert.Single(telegram.Sent, m => m.Body.Contains("АЗС-control"));
        Assert.StartsWith("http://telegram.test/bot123:secret/sendMessage", msg.Url);
        Assert.Contains("-100500", msg.Body);
        Assert.Contains("Разрыв счётчика", msg.Body);
        Assert.Contains("Недостача при приёме", msg.Body);
        Assert.Contains("Заканчивается", msg.Body);

        // Deduplication: a second run finds nothing new and sends nothing more.
        var sentBefore = telegram.Sent.Count;
        var again = await (await owner.PostAsync("/api/v1/ui/alerts/run", null)).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(0, again!["found"]!.GetValue<int>());
        Assert.Equal(sentBefore, telegram.Sent.Count);

        // Status workflow.
        var id = gap["id"]!.GetValue<long>();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync($"/api/v1/ui/alerts/{id}/status", new { status = "closed" })).StatusCode);
        Assert.Equal("closed", await fx.QueryAsync<string>("SELECT status FROM alerts WHERE id = @id", new { id }));
    }

    /// <summary>
    /// A station uploading its history sends the newest shifts first: the counter of shift 3 must not be compared with
    /// shift 1 while shift 2 is missing. A totalizer passing 999 999,99 → 0 is not minus a million litres. An alert the
    /// data no longer supports closes by itself.
    /// </summary>
    [PostgresFact]
    public async Task Counter_checks_wait_for_the_previous_shift_and_survive_a_rollover()
    {
        var st = await fx.EnrollAsync("АЗС-счётчики");
        var users = fx.Factory!.Services.GetRequiredService<UserService>();
        try { await users.CreateAsync("counter-owner", "o", Roles.Owner, "control-password-1"); } catch (Npgsql.PostgresException) { }
        var owner = fx.Factory.CreateClient();
        owner.DefaultRequestHeaders.Add(UiAuthSetup.CsrfHeader, "1");
        (await owner.PostAsJsonAsync("/api/v1/ui/auth/login", new { login = "counter-owner", password = "control-password-1" })).EnsureSuccessStatusCode();
        var t = DateTimeOffset.UtcNow.AddDays(-3);
        RecordChange Shift(int id) => Row("sysSessions", $"{id}", new JsonObject { ["SessionID"] = id, ["SessionNum"] = id, ["UserName"] = "Оператор", ["StartDateTime"] = Ts(t.AddHours(10 * id)), ["EndDateTime"] = Ts(t.AddHours(10 * id + 9)), ["Deleted"] = 0 });
        RecordChange Hose(int session, decimal start, decimal end) => Row("flSesHoses", $"{session}|1", new JsonObject { ["SessionID"] = session, ["HoseID"] = 1, ["StartAdder"] = start, ["EndAdder"] = end, ["PumpNum"] = 1, ["NumInPump"] = 1 });
        RecordChange Sale(int id, int session, decimal volume) => Row("flOrders", $"{id}", new JsonObject
        {
            ["OrderID"] = id, ["SessionID"] = session, ["OrderNum"] = id, ["DateTime"] = Ts(t.AddHours(10 * session + 1)), ["FuelID"] = 1, ["TankID"] = 1, ["HoseID"] = 1, ["PaymentModeID"] = 1,
            ["Remark"] = "", ["Price"] = 50m, ["PricePump"] = 50m, ["Volume"] = volume, ["Amount"] = volume * 50, ["AmountCash"] = volume * 50, ["AmountCashless"] = 0, ["AmountDiscount"] = 0,
        });
        async Task SendAsync(params RecordChange[] items)
        {
            var batch = new BatchEnvelope { BatchId = Guid.NewGuid(), Seq = 1, CreatedAt = DateTimeOffset.UtcNow, AgentVersion = "t", Items = items.ToList() };
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true)) JsonSerializer.Serialize(gz, batch, Protocol.Json);
            var content = new ByteArrayContent(ms.ToArray());
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            content.Headers.ContentEncoding.Add("gzip");
            (await fx.Client(st.Token).PostAsync("/api/v1/batches", content)).EnsureSuccessStatusCode();
            await fx.WaitProcessedAsync();
        }
        Task<long> CountAsync(string rule, string status = "new") =>
            fx.QueryAsync<long>("SELECT count(*) FROM alerts WHERE station_id = @s AND rule_code = @rule AND status = @status", new { s = st.StationId, rule, status });

        // Newest first: shifts 1 and 3 (the counter rolls over during shift 3), shift 2 still missing.
        await SendAsync(Shift(1), Shift(3), Hose(1, 999_000m, 999_900m), Hose(3, 999_980m, 30m), Sale(1, 1, 900m), Sale(3, 3, 50m));
        // The alert an older version raised in this situation (shift 3 against shift 1).
        await fx.QueryAsync<long>("""
            INSERT INTO alerts (station_id, rule_code, dedupe_key, severity, title, value, session_id) VALUES (@s, 'counter_gap', '1/3', 'critical', 'старое', 80, 3) RETURNING id
            """, new { s = st.StationId });
        (await owner.PostAsync("/api/v1/ui/alerts/run", null)).EnsureSuccessStatusCode();
        Assert.Equal(0, await CountAsync("counter_gap"));             // the stale one is gone
        Assert.Equal(0, await CountAsync("counter_gap", "closed"));
        Assert.Equal(0, await CountAsync("counter_mismatch"));        // 999 980 → 30 is 50 l, as the orders

        // Shift 2 arrives and leaves 80 l between its end and the start of shift 3 unaccounted.
        await SendAsync(Shift(2), Hose(2, 999_900m, 999_900m));
        (await owner.PostAsync("/api/v1/ui/alerts/run", null)).EnsureSuccessStatusCode();
        Assert.Equal(1, await CountAsync("counter_gap"));             // a real gap of the same shift and hose is raised
        Assert.Equal(80m, await fx.QueryAsync<decimal>("SELECT value FROM alerts WHERE station_id = @s AND rule_code = 'counter_gap'", new { s = st.StationId }));
    }

    [PostgresFact]
    public async Task Tank_plan_days_of_cover_and_order()
    {
        var station = await StationAsync("АЗС-tanks");
        var users = fx.Factory!.Services.GetRequiredService<UserService>();
        try { await users.CreateAsync("tank-owner", "o", Roles.Owner, "control-password-1"); } catch (Npgsql.PostgresException) { }
        var owner = fx.Factory.CreateClient();
        owner.DefaultRequestHeaders.Add(UiAuthSetup.CsrfHeader, "1");
        (await owner.PostAsJsonAsync("/api/v1/ui/auth/login", new { login = "tank-owner", password = "control-password-1" })).EnsureSuccessStatusCode();

        var r = await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/analytics/tanks?stations={station}");
        var t = r!["tanks"]!.AsArray().Single()!;
        Assert.Equal(1000m, t["book_volume"]!.GetValue<decimal>());     // 1040 + 0 − 40
        Assert.Equal(0.1m, t["fill_share"]!.GetValue<decimal>());
        // Last 28 days: 26 × 1000 + 1000 (shift 1: 10 × 100) + 40 (shift 2) = 27 040 l → 965.7 l/day
        Assert.Equal(965.7m, t["per_day"]!.GetValue<decimal>());
        Assert.Equal(0.7m, t["days_to_min"]!.GetValue<decimal>());       // (1000 − 300) / 965.7
        Assert.Equal("critical", t["status"]!.GetValue<string>());
        // 95 % of 10 000 − (1000 − 2 days × 965.7 → 0) = 9 500
        Assert.Equal(9500m, t["recommended_order"]!.GetValue<decimal>());

        // Settings: a manager-entered minimum moves the threshold.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync($"/api/v1/ui/tanks/{station}/1", new { min_liters = 500, lead_time_days = 1, safety_days = 0 })).StatusCode);
        var r2 = await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/analytics/tanks?stations={station}");
        Assert.Equal(0.5m, r2!["tanks"]![0]!["days_to_min"]!.GetValue<decimal>()); // (1000 − 500) / 965.7
    }
}
