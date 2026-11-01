using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Topaz.Central.Processing;
using Topaz.Central.Ui;
using Topaz.Contracts;
using Xunit;

namespace Topaz.Central.Tests;

/// <summary>Reference fuel types and the confirmation of station fuel mappings (docs/web.md, «Справочник видов топлива»).</summary>
public sealed class FuelCatalogTests(CentralFixture fx) : IClassFixture<CentralFixture>
{
    private const string Password = "correct-horse-1";
    private const string Period = "from=2026-10-01&to=2026-10-31&compare=none";

    private async Task<HttpClient> LoginAsync(string login, string role, params Guid[] stations)
    {
        var users = fx.Factory!.Services.GetRequiredService<UserService>();
        try { await users.CreateAsync(login, login, role, Password, stations); } catch (Npgsql.PostgresException) { /* exists */ }
        var client = fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(UiAuthSetup.CsrfHeader, "1");
        (await client.PostAsJsonAsync("/api/v1/ui/auth/login", new { login, password = Password })).EnsureSuccessStatusCode();
        return client;
    }

    private static string Hash(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    private static RecordChange Row(string dataset, string key, JsonObject data) => new() { Dataset = dataset, Key = key, Hash = Hash(dataset + key + data.ToJsonString()), Data = data };

    /// <summary>A station with the given fuels (id → Topaz name), each sold once on 2026-10-10: 10 l × id.</summary>
    private async Task<Guid> StationAsync(string name, params (int Id, string Name)[] fuels)
    {
        var st = await fx.EnrollAsync(name);
        var items = new List<RecordChange>
        {
            Row("sysSessions", "1", new JsonObject { ["SessionID"] = 1, ["SessionNum"] = 1, ["UserName"] = "Иванов", ["StartDateTime"] = "2026-10-10T08:00:00.0000+03:00", ["EndDateTime"] = "2026-10-11T08:00:00.0000+03:00", ["Deleted"] = 0 }),
            Row("dcPaymentModes", "1", new JsonObject { ["PaymentModeID"] = 1, ["Name"] = "Наличные" }),
            Row("dcFuels", "0", new JsonObject { ["FuelID"] = 0, ["Name"] = "<не выбрано>", ["Deleted"] = 1 }),
        };
        foreach (var (id, fuelName) in fuels)
        {
            items.Add(Row("dcFuels", id.ToString(), new JsonObject { ["FuelID"] = id, ["Name"] = fuelName, ["Deleted"] = 0 }));
            items.Add(Row("flOrders", id.ToString(), new JsonObject
            {
                ["OrderID"] = id, ["SessionID"] = 1, ["OrderNum"] = id, ["DateTime"] = "2026-10-10T12:00:00.0000+03:00", ["FuelID"] = id, ["TankID"] = 1, ["HoseID"] = 1,
                ["Volume"] = 10m * id, ["Amount"] = 500m * id, ["Price"] = 50m, ["PaymentModeID"] = 1, ["Remark"] = "", ["AmountCash"] = 500m * id, ["AmountCashless"] = 0, ["AmountDiscount"] = 0,
            }));
        }
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

    private static JsonObject Fuel(JsonObject catalog, Guid station, int fuelId) =>
        catalog["fuels"]!.AsArray().Select(f => f!.AsObject()).Single(f => f["station_id"]!.GetValue<Guid>() == station && f["fuel_id"]!.GetValue<long>() == fuelId);

    private static Dictionary<string, decimal> LitersByType(JsonObject fuel) =>
        fuel["by_type"]!.AsArray().ToDictionary(t => t!["fuel_type"]!.GetValue<string>(), t => t!["liters"]!.GetValue<decimal>());

    [PostgresFact]
    public async Task New_fuels_are_suggested_learned_from_other_stations_and_confirmed()
    {
        var a = await StationAsync("АЗС-fc-A", (1, "Аи-95 Ultimate"), (2, "Аи92"));
        var owner = await LoginAsync("fc-owner", Roles.Owner);

        // Suggestion by name, not confirmed yet; reports already use it. The "<не выбрано>" placeholder needs nothing.
        var cat = (await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/catalog/fuels?station={a}"))!;
        Assert.Equal(2, cat["fuels"]!.AsArray().Count);
        var ultimate = Fuel(cat, a, 1);
        Assert.Equal("AI95", ultimate["suggested_type"]!.GetValue<string>());
        Assert.Equal("name", ultimate["suggestion_source"]!.GetValue<string>());
        Assert.False(ultimate["confirmed"]!.GetValue<bool>());
        Assert.Equal(2, (await owner.GetFromJsonAsync<JsonObject>("/api/v1/ui/meta"))!["unmapped_fuels"]!.GetValue<int>());
        Assert.Equal(10m, LitersByType((await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/analytics/fuel?stations={a}&{Period}"))!)["AI95"]);

        // The owner adds "АИ-95 премиум" and confirms the Ultimate fuel as that type.
        var created = await owner.PostAsJsonAsync("/api/v1/ui/catalog/fuel-types", new { name = "АИ-95 премиум" });
        created.EnsureSuccessStatusCode();
        var premium = (await created.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/api/v1/ui/catalog/fuel-types", new { name = "аи-95 ПРЕМИУМ" })).StatusCode);
        (await owner.PutAsJsonAsync($"/api/v1/ui/catalog/fuels/{a}/1", new { fuel_type = premium })).EnsureSuccessStatusCode();

        var byType = LitersByType((await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/analytics/fuel?stations={a}&{Period}"))!);
        Assert.Equal(10m, byType[premium]);
        Assert.False(byType.ContainsKey("AI95"));
        Assert.Equal(20m, byType["AI92"]);
        // The report filter is the reference type, not the Topaz name.
        var filtered = await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/analytics/overview?stations={a}&{Period}&fuel={premium}");
        Assert.Equal(10m, filtered!["current"]!["liters"]!.GetValue<decimal>());

        // A new station with the same name written differently gets the learned suggestion.
        var b = await StationAsync("АЗС-fc-B", (5, "АИ 95 ultimate"), (6, "Метан"));
        cat = (await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/catalog/fuels?station={b}&unconfirmed=1"))!;
        var learned = Fuel(cat, b, 5);
        Assert.Equal(premium, learned["suggested_type"]!.GetValue<string>());
        Assert.Equal("learned", learned["suggestion_source"]!.GetValue<string>());
        Assert.Equal("АЗС-fc-A", learned["suggested_from"]!.GetValue<string>());
        Assert.Equal("CNG", Fuel(cat, b, 6)["suggested_type"]!.GetValue<string>());

        // Alerts: one per station with unconfirmed fuels; confirming all closes them automatically.
        var detector = fx.Factory!.Services.GetRequiredService<AnomalyDetector>();
        await detector.RunAsync(CancellationToken.None);
        Assert.Equal(2, await fx.QueryAsync<int>("SELECT count(*)::int FROM alerts WHERE rule_code = 'fuel_unmapped' AND status = 'new'"));
        var confirmed = await owner.PostAsJsonAsync("/api/v1/ui/catalog/fuels/confirm", new { station_id = b });
        Assert.Equal(2, (await confirmed.Content.ReadFromJsonAsync<JsonObject>())!["confirmed"]!.GetValue<int>());
        Assert.Equal(premium, await fx.QueryAsync<string>("SELECT fuel_type FROM station_fuel_map WHERE station_id = @b AND fuel_id = 5", new { b }));
        Assert.Equal("fc-owner", await fx.QueryAsync<string>("SELECT confirmed_by FROM station_fuel_map WHERE station_id = @b AND fuel_id = 5", new { b }));
        await detector.RunAsync(CancellationToken.None);
        Assert.Equal("closed", await fx.QueryAsync<string>("SELECT status FROM alerts WHERE rule_code = 'fuel_unmapped' AND station_id = @b", new { b }));
        Assert.Equal("new", await fx.QueryAsync<string>("SELECT status FROM alerts WHERE rule_code = 'fuel_unmapped' AND station_id = @a", new { a }));
        Assert.Equal(1, (await owner.GetFromJsonAsync<JsonObject>("/api/v1/ui/meta"))!["unmapped_fuels"]!.GetValue<int>());

        // Removing a confirmation brings the fuel back to its suggestion.
        (await owner.PutAsJsonAsync($"/api/v1/ui/catalog/fuels/{b}/6", new { fuel_type = (string?)null })).EnsureSuccessStatusCode();
        Assert.True(Fuel((await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/catalog/fuels?station={b}"))!, b, 6)["needs_confirmation"]!.GetValue<bool>());
        Assert.Contains("fuel_type_mapped", await owner.GetStringAsync("/api/v1/ui/audit"));
    }

    [PostgresFact]
    public async Task Reference_types_rename_hide_and_delete_rules()
    {
        var s = await StationAsync("АЗС-fc-R", (1, "ДТ летнее"));
        var owner = await LoginAsync("fc-owner2", Roles.Owner);

        var add = await owner.PostAsJsonAsync("/api/v1/ui/catalog/fuel-types", new { name = "ДТ зимнее" });
        var code = (await add.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>();

        // Rename is visible in the reports at once (names come from the reference).
        (await owner.PutAsJsonAsync("/api/v1/ui/catalog/fuel-types/DT", new { name = "Дизельное топливо" })).EnsureSuccessStatusCode();
        var fuel = (await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/analytics/fuel?stations={s}&{Period}"))!;
        Assert.Equal("Дизельное топливо", fuel["by_type"]!.AsArray().Single()!["fuel_name"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync($"/api/v1/ui/catalog/fuel-types/{code}", new { name = "дизельное топливо" })).StatusCode);

        // Used types cannot be deleted (DT is suggested for the station), unused ones can; "Прочее" stays.
        Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync("/api/v1/ui/catalog/fuel-types/DT")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.DeleteAsync("/api/v1/ui/catalog/fuel-types/OTHER")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/v1/ui/catalog/fuel-types/OTHER", new { is_active = false })).StatusCode);
        (await owner.PutAsJsonAsync($"/api/v1/ui/catalog/fuel-types/{code}", new { is_active = false })).EnsureSuccessStatusCode();
        var types = (await owner.GetFromJsonAsync<JsonArray>("/api/v1/ui/catalog/fuel-types"))!;
        Assert.False(types.Single(t => t!["code"]!.GetValue<string>() == code)!["is_active"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/ui/catalog/fuel-types/{code}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/v1/ui/catalog/fuels/{s}/1", new { fuel_type = code })).StatusCode);

        // Only the owner edits the reference and the mappings; others read within their stations.
        var other = await StationAsync("АЗС-fc-hidden", (1, "Аи92"));
        foreach (var role in new[] { Roles.Manager, Roles.Viewer })
        {
            var u = await LoginAsync("fc-" + role, role, s);
            Assert.Equal(HttpStatusCode.Forbidden, (await u.PostAsJsonAsync("/api/v1/ui/catalog/fuel-types", new { name = "x" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await u.PutAsJsonAsync("/api/v1/ui/catalog/fuel-types/DT", new { name = "x" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await u.DeleteAsync("/api/v1/ui/catalog/fuel-types/CNG")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await u.PutAsJsonAsync($"/api/v1/ui/catalog/fuels/{s}/1", new { fuel_type = "DT" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await u.PostAsJsonAsync("/api/v1/ui/catalog/fuels/confirm", new { })).StatusCode);
            var list = await u.GetStringAsync("/api/v1/ui/catalog/fuels");
            Assert.Contains("АЗС-fc-R", list);
            Assert.DoesNotContain("АЗС-fc-hidden", list);
            Assert.DoesNotContain(other.ToString(), await u.GetStringAsync($"/api/v1/ui/catalog/fuels?station={other}"));
            Assert.Equal(HttpStatusCode.OK, (await u.GetAsync("/api/v1/ui/catalog/fuel-types")).StatusCode);
        }
    }
}
