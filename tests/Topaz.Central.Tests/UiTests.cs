using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Topaz.Central.Ui;
using Topaz.Contracts;
using Xunit;

namespace Topaz.Central.Tests;

public sealed class UiTests(CentralFixture fx) : IClassFixture<CentralFixture>
{
    private const string Password = "correct-horse-1";

    private async Task<HttpClient> LoginAsync(string login, string role, params Guid[] stations)
    {
        var users = fx.Factory!.Services.GetRequiredService<UserService>();
        try { await users.CreateAsync(login, login, role, Password, stations); } catch (Npgsql.PostgresException) { /* exists */ }
        var client = fx.Factory.CreateClient(); // keeps cookies
        client.DefaultRequestHeaders.Add(UiAuthSetup.CsrfHeader, "1");
        (await client.PostAsJsonAsync("/api/v1/ui/auth/login", new { login, password = Password })).EnsureSuccessStatusCode();
        return client;
    }

    private static string Hash(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static RecordChange Row(string dataset, string key, JsonObject data) =>
        new() { Dataset = dataset, Key = key, Hash = Hash(dataset + key + data.ToJsonString()), Data = data };

    private static RecordChange Order(int id, string at, decimal volume, decimal amount, int mode, string remark = "", decimal cashless = 0, decimal discount = 0) =>
        Row("flOrders", id.ToString(), new JsonObject
        {
            ["OrderID"] = id, ["SessionID"] = 1, ["OrderNum"] = id, ["DateTime"] = at, ["FuelID"] = 1, ["TankID"] = 1, ["HoseID"] = 1,
            ["Volume"] = volume, ["Amount"] = amount, ["Price"] = 50m, ["PaymentModeID"] = mode, ["Remark"] = remark,
            ["AmountCash"] = cashless > 0 ? 0 : amount, ["AmountCashless"] = cashless, ["AmountDiscount"] = discount,
        });

    /// <summary>Enrolls a station with a small, hand-countable data set.</summary>
    private async Task<Guid> StationAsync(string name, string timeZone)
    {
        var st = await fx.EnrollAsync(name);
        var client = fx.Client(st.Token);
        var items = new List<RecordChange>
        {
            Row("dcFuels", "1", new JsonObject { ["FuelID"] = 1, ["Name"] = "Аи92", ["Deleted"] = 0 }),
            Row("dcPaymentModes", "1", new JsonObject { ["PaymentModeID"] = 1, ["Name"] = "Наличные" }),
            Row("dcPaymentModes", "2", new JsonObject { ["PaymentModeID"] = 2, ["Name"] = "Терминал" }),
            Row("dcPaymentModes", "3", new JsonObject { ["PaymentModeID"] = 3, ["Name"] = "Тех. прокачка" }),
            Row("sysSessions", "1", new JsonObject { ["SessionID"] = 1, ["SessionNum"] = 1, ["UserName"] = "Иванов", ["StartDateTime"] = "2026-10-10T08:00:00.0000+03:00", ["EndDateTime"] = "2026-10-11T08:00:00.0000+03:00", ["Deleted"] = 0 }),
            Order(1, "2026-10-10T09:00:00.0000+03:00", 20m, 1000m, 1),
            Order(2, "2026-10-10T10:00:00.0000+03:00", 40m, 1950m, 2, "Б", cashless: 1950m, discount: 50m),
            Order(3, "2026-10-10T11:00:00.0000+03:00", 0m, 0m, 1),           // aborted
            Order(4, "2026-10-10T12:00:00.0000+03:00", 10m, 0m, 3),          // technical pumping
            Order(5, "2026-10-31T23:30:00.0000+03:00", 30m, 1500m, 1),       // local date depends on the station zone
        };
        var batch = new BatchEnvelope { BatchId = Guid.NewGuid(), Seq = 1, CreatedAt = DateTimeOffset.UtcNow, AgentVersion = "t", Items = items };
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true)) JsonSerializer.Serialize(gz, batch, Protocol.Json);
        var content = new ByteArrayContent(ms.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        (await client.PostAsync("/api/v1/batches", content)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/v1/heartbeat", new HeartbeatRequest { AgentVersion = "t", TimeZone = timeZone }, Protocol.Json)).EnsureSuccessStatusCode();
        await fx.WaitProcessedAsync();
        return st.StationId;
    }

    [PostgresFact]
    public async Task Login_lockout_csrf_and_unauthenticated_access()
    {
        var anon = fx.Factory!.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/ui/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/ui/analytics/overview")).StatusCode);

        var users = fx.Factory.Services.GetRequiredService<UserService>();
        await users.CreateAsync("lock-me", "x", Roles.Viewer, Password);
        var c = fx.Factory.CreateClient();
        c.DefaultRequestHeaders.Add(UiAuthSetup.CsrfHeader, "1");
        for (var i = 0; i < UserService.MaxFailedLogins; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/v1/ui/auth/login", new { login = "lock-me", password = "wrong-password" })).StatusCode);
        // Locked now: even the right password is refused.
        Assert.Equal((HttpStatusCode)423, (await c.PostAsJsonAsync("/api/v1/ui/auth/login", new { login = "lock-me", password = Password })).StatusCode);

        var noCsrf = fx.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await noCsrf.PostAsJsonAsync("/api/v1/ui/auth/login", new { login = "x", password = "y" })).StatusCode);

        var me = await (await LoginAsync("owner-a", Roles.Owner)).GetFromJsonAsync<JsonObject>("/api/v1/ui/auth/me");
        Assert.Equal("owner", me!["role"]!.GetValue<string>());
        Assert.True(me["sees_money"]!.GetValue<bool>());
    }

    [PostgresFact]
    public async Task Unknown_paths_never_answer_200()
    {
        var c = fx.Factory!.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync("/foo/api/v1/batches", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/v1/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync("/some/spa/route", null)).StatusCode);
    }

    [PostgresFact]
    public async Task Kpis_follow_metric_definitions_and_station_time_zone()
    {
        var msk = await StationAsync("АЗС-ui-msk", "Europe/Moscow");
        var ekb = await StationAsync("АЗС-ui-ekb", "Asia/Yekaterinburg");
        var owner = await LoginAsync("owner-k", Roles.Owner);

        var o = await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/analytics/overview?stations={msk}&from=2026-10-01&to=2026-10-31&compare=none");
        var k = o!["current"]!;
        Assert.Equal(4450m, k["fuel_revenue"]!.GetValue<decimal>());   // 1000 + 1950 + 0 + 0 + 1500
        Assert.Equal(90m, k["liters"]!.GetValue<decimal>());           // tech pumping (10 l) excluded
        Assert.Equal(3, k["orders"]!.GetValue<int>());                 // aborted and tech pumping excluded
        Assert.Equal(1, k["aborted"]!.GetValue<int>());
        Assert.Equal(10m, k["service_liters"]!.GetValue<decimal>());
        Assert.Equal(1950m, k["cashless"]!.GetValue<decimal>());
        Assert.Equal(50m, k["discounts"]!.GetValue<decimal>());
        Assert.Equal(30m, k["avg_fill"]!.GetValue<decimal>());
        Assert.Equal(0.4382m, k["cashless_share"]!.GetValue<decimal>());

        // 31.10 23:30 Moscow = 01.11 01:30 Yekaterinburg: the same order falls into November there.
        var ekbOct = await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/analytics/overview?stations={ekb}&from=2026-10-01&to=2026-10-31&compare=none");
        Assert.Equal(2950m, ekbOct!["current"]!["fuel_revenue"]!.GetValue<decimal>());
        var ekbNov = await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/analytics/overview?stations={ekb}&from=2026-11-01&to=2026-11-30&compare=none");
        Assert.Equal(1500m, ekbNov!["current"]!["fuel_revenue"]!.GetValue<decimal>());

        var shifts = await owner.GetFromJsonAsync<JsonArray>($"/api/v1/ui/shifts?stations={msk}&from=2026-10-01&to=2026-10-31");
        Assert.Equal("Иванов", Assert.Single(shifts!)!["user_name"]!.GetValue<string>());
        var card = await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/shifts/{msk}/1");
        Assert.Equal(5, card!["orders"]!.AsArray().Count);             // a shift card shows all its orders, whatever their date

        var csv = await owner.GetStringAsync($"/api/v1/ui/data/fuel-sales?stations={msk}&from=2026-10-01&to=2026-10-31&format=csv");
        var lines = csv.TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(6, lines.Length);                                // header + 5 orders
        Assert.Contains("1950,00", csv);                                // decimal comma for Russian Excel
    }

    [PostgresFact]
    public async Task Managers_see_only_their_stations_everywhere()
    {
        var mine = await StationAsync("АЗС-ui-mine", "Europe/Moscow");
        var other = await StationAsync("АЗС-ui-other", "Europe/Moscow");
        var manager = await LoginAsync("manager-m", Roles.Manager, mine);
        const string period = "from=2026-10-01&to=2026-10-31";

        foreach (var path in new[] { "analytics/overview", "analytics/stations", "shifts", "shifts/operators", "data/fuel-sales", "data/shop-lines", "data/fuel-receipts", "data/events", "data/cash" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"/api/v1/ui/{path}?stations={other}&{period}")).StatusCode);
            var body = await manager.GetStringAsync($"/api/v1/ui/{path}?{period}");
            Assert.DoesNotContain(other.ToString(), body);
            Assert.DoesNotContain("АЗС-ui-other", body);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"/api/v1/ui/shifts/{other}/1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"/api/v1/ui/shifts/{mine}/1")).StatusCode);

        var meta = await manager.GetStringAsync("/api/v1/ui/meta");
        Assert.Contains("АЗС-ui-mine", meta);
        Assert.DoesNotContain("АЗС-ui-other", meta);
        Assert.DoesNotContain("АЗС-ui-other", await manager.GetStringAsync("/api/v1/ui/stations"));

        // Fuel catalog: lists only the manager's stations, editing is owner-only.
        Assert.DoesNotContain("АЗС-ui-other", await manager.GetStringAsync("/api/v1/ui/catalog/fuels"));
        Assert.DoesNotContain(other.ToString(), await manager.GetStringAsync($"/api/v1/ui/catalog/fuels?station={other}"));
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PutAsJsonAsync($"/api/v1/ui/catalog/fuels/{mine}/1", new { fuel_type = "AI92" })).StatusCode);

        // Owner-only endpoints.
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/v1/ui/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PatchAsJsonAsync($"/api/v1/ui/stations/{mine}", new { display_name = "x" })).StatusCode);
    }

    [PostgresFact]
    public async Task Superadmin_manages_users_and_changes_apply_immediately()
    {
        var station = await StationAsync("АЗС-ui-users", "Europe/Moscow");
        var owner = await LoginAsync("owner-u", Roles.Superadmin); // users and the audit log are superadmin-only

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/ui/users", new { login = "short", name = "s", role = "viewer", password = "123" })).StatusCode);
        var created = await owner.PostAsJsonAsync("/api/v1/ui/users", new { login = "viewer-v", name = "Смотритель", role = "viewer", password = Password, stations = new[] { station } });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/api/v1/ui/users", new { login = "VIEWER-V", name = "x", role = "viewer", password = Password })).StatusCode);

        var viewer = fx.Factory!.CreateClient();
        viewer.DefaultRequestHeaders.Add(UiAuthSetup.CsrfHeader, "1");
        (await viewer.PostAsJsonAsync("/api/v1/ui/auth/login", new { login = "viewer-v", password = Password })).EnsureSuccessStatusCode();
        Assert.False((await viewer.GetFromJsonAsync<JsonObject>("/api/v1/ui/auth/me"))!["sees_money"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/ui/analytics/overview")).StatusCode);

        // Deactivation kills the existing session (security stamp), no re-login needed to take effect.
        (await owner.PatchAsJsonAsync($"/api/v1/ui/users/{id}", new { is_active = false })).EnsureSuccessStatusCode();
        await Task.Delay(TimeSpan.FromSeconds(21)); // user cache TTL
        Assert.Equal(HttpStatusCode.Unauthorized, (await viewer.GetAsync("/api/v1/ui/analytics/overview")).StatusCode);

        // Station display settings and audit.
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PatchAsJsonAsync($"/api/v1/ui/stations/{station}", new { time_zone = "Mars/Olympus" })).StatusCode);
        (await owner.PatchAsJsonAsync($"/api/v1/ui/stations/{station}", new { display_name = "АЗС №1 Центральная", time_zone = "Asia/Yekaterinburg" })).EnsureSuccessStatusCode();
        Assert.Contains("АЗС №1 Центральная", await owner.GetStringAsync("/api/v1/ui/meta"));
        Assert.Equal("Asia/Yekaterinburg", await fx.QueryAsync<string>("SELECT time_zone FROM stations WHERE id = @station", new { station }));
        Assert.Contains("user_updated", await owner.GetStringAsync("/api/v1/ui/audit"));
    }
}
