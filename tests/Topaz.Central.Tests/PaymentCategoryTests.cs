using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Topaz.Central.Ui;
using Topaz.Contracts;
using Xunit;

namespace Topaz.Central.Tests;

/// <summary>
/// 1.7: payment categories — cash, bank card, contract (ведомости), fuel cards, unpaid — guessed from the payment mode
/// name (Topaz has no type), overridable by the owner, used by every report, the payment filter and the contract report.
/// </summary>
public sealed class PaymentCategoryTests(CentralFixture fx) : IClassFixture<CentralFixture>
{
    private const string Password = "pay-password-1";
    private static string Hash(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    private static RecordChange Row(string dataset, string key, JsonObject data) => new() { Dataset = dataset, Key = key, Hash = Hash(dataset + key + data.ToJsonString()), Data = data };
    private static readonly DateTimeOffset Day = new(2026, 9, 15, 12, 0, 0, TimeSpan.FromHours(3));
    private const string Period = "from=2026-09-01&to=2026-09-30&compare=none";

    private async Task<HttpClient> LoginAsync(string login, string role, Guid? org)
    {
        var users = fx.Factory!.Services.GetRequiredService<UserService>();
        try { await users.CreateAsync(login, login, role, Password, [], org); } catch (PostgresException) { /* exists */ }
        var c = fx.Factory.CreateClient();
        c.DefaultRequestHeaders.Add(UiAuthSetup.CsrfHeader, "1");
        (await c.PostAsJsonAsync("/api/v1/ui/auth/login", new { login, password = Password })).EnsureSuccessStatusCode();
        return c;
    }

    /// <summary>A station in an organization of its own: 500 ₽ cash, 1 000 ₽ card (one without and one with Topaz's "Б"),
    /// 1 000 ₽ contract, 500 ₽ fuel card and unpaid tech pumping.</summary>
    private async Task<(Guid Station, HttpClient Owner, Guid Org)> StationAsync(string name)
    {
        var super = await LoginAsync("pay-super", Roles.Superadmin, null);
        var created = await super.PostAsJsonAsync("/api/v1/ui/organizations", new { name = "Орг " + name });
        created.EnsureSuccessStatusCode();
        var org = (await created.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        var st = await fx.EnrollAsync(name, org);
        var id = 0;
        JsonObject Sale(int mode, decimal liters, decimal amount, string remark = "", decimal cashless = 0, int? fiscal = null) => new()
        {
            ["OrderID"] = ++id, ["SessionID"] = 1, ["OrderNum"] = id, ["DateTime"] = Day.AddMinutes(id).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffzzz"), ["FuelID"] = 1, ["TankID"] = 1,
            ["HoseID"] = 1, ["PaymentModeID"] = mode, ["Remark"] = remark, ["Price"] = 50m, ["PricePump"] = 50m, ["Volume"] = liters, ["Amount"] = amount,
            ["AmountCash"] = amount - cashless, ["AmountCashless"] = cashless, ["AmountDiscount"] = 0, ["FiscalNumber"] = fiscal,
        };
        var items = new List<RecordChange>
        {
            Row("dcFuels", "1", new JsonObject { ["FuelID"] = 1, ["Name"] = "ДТ", ["Deleted"] = 0 }),
            Row("dcPaymentModes", "1", new JsonObject { ["PaymentModeID"] = 1, ["Name"] = "НАЛИЧКА" }),
            Row("dcPaymentModes", "2", new JsonObject { ["PaymentModeID"] = 2, ["Name"] = "Банк. Терминал" }),
            Row("dcPaymentModes", "3", new JsonObject { ["PaymentModeID"] = 3, ["Name"] = "ВЕДОМОСТИ" }),
            Row("dcPaymentModes", "4", new JsonObject { ["PaymentModeID"] = 4, ["Name"] = "Е1КАРД" }),
            Row("dcPaymentModes", "5", new JsonObject { ["PaymentModeID"] = 5, ["Name"] = "Тех. прокачка" }),
            Row("dcPaymentModes", "6", new JsonObject { ["PaymentModeID"] = 6, ["Name"] = "Наличные" }),
        };
        foreach (var s in new[] { Sale(1, 10, 500, fiscal: 1), Sale(2, 10, 500), Sale(3, 20, 1000), Sale(4, 10, 500), Sale(5, 5, 0), Sale(6, 10, 500, "Б", 500, fiscal: 2) })
            items.Add(Row("flOrders", s["OrderID"]!.ToString(), s));
        var batch = new BatchEnvelope { BatchId = Guid.NewGuid(), Seq = 1, CreatedAt = DateTimeOffset.UtcNow, AgentVersion = "t", Items = items };
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true)) JsonSerializer.Serialize(gz, batch, Protocol.Json);
        var content = new ByteArrayContent(ms.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        (await fx.Client(st.Token).PostAsync("/api/v1/batches", content)).EnsureSuccessStatusCode();
        await fx.WaitProcessedAsync();
        return (st.StationId, await LoginAsync("pay-owner-" + name, Roles.Owner, org), org);
    }

    [PostgresFact]
    public async Task Categories_are_guessed_from_the_names_found_at_real_stations()
    {
        var expected = new Dictionary<string, string>
        {
            ["НАЛИЧКА"] = "cash", ["Наличные + Дисконтная карта"] = "cash", ["НАЛИЧКА + Дисконтная карта"] = "cash", ["Дисконтная(НАЛИЧКА)"] = "cash",
            ["СКИДКА"] = "cash", ["Банковская Карта"] = "card", ["Банк. Терминал"] = "card", ["Банк. Терминал + Дисконтная карта"] = "card",
            ["Дисконтная(БАНК.КАРТА)"] = "card", ["Терминал Сбербанк"] = "card", ["АльфаБанк"] = "card", ["СБП ТБанк"] = "card",
            ["ВЕДОМОСТИ"] = "contract", ["Ведомость"] = "contract", ["Ведомость/Карты"] = "contract", ["E1CARD"] = "fuel_card", ["Е1КАРД"] = "fuel_card",
            ["Тех. прокачка"] = "none", ["Переливы"] = "none", ["Бесплатно"] = "none",
        };
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        foreach (var (name, category) in expected)
            Assert.True(category == await c.ExecuteScalarAsync<string>("SELECT payment_category_guess(@name)", new { name }), name);
    }

    [PostgresFact]
    public async Task Reports_split_money_by_category_and_follow_the_owners_marks()
    {
        var (st, owner, _) = await StationAsync("АЗС-оплаты");
        async Task<JsonNode> CurrentAsync(string extra = "") =>
            (await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/analytics/overview?stations={st}&{Period}{extra}"))!["current"]!;

        var cur = await CurrentAsync();
        Assert.Equal(500m, cur["cash"]!.GetValue<decimal>());        // НАЛИЧКА
        Assert.Equal(1000m, cur["card"]!.GetValue<decimal>());       // terminal without "Б" + "Наличные" paid cashless
        Assert.Equal(1000m, cur["contract"]!.GetValue<decimal>());   // ВЕДОМОСТИ, recorded by Topaz as cash
        Assert.Equal(500m, cur["fuel_card"]!.GetValue<decimal>());
        Assert.Equal(2500m, cur["cashless"]!.GetValue<decimal>());

        // The payment filter: contract clients only.
        Assert.Equal(1000m, (await CurrentAsync("&pay=contract"))["fuel_revenue"]!.GetValue<decimal>());
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync($"/api/v1/ui/analytics/overview?stations={st}&{Period}&pay=barter")).StatusCode);

        // Money by day in Finance.
        var finance = await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/analytics/finance?stations={st}&{Period}");
        var day = finance!["cash"]!.AsArray().Single(d => d!["day"]!.GetValue<string>() == "2026-09-15")!;
        Assert.Equal(500m, day["fuel_cash"]!.GetValue<decimal>());
        Assert.Equal(1000m, day["fuel_contract"]!.GetValue<decimal>());

        // Contract report and its CSV.
        var report = await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/analytics/contracts?stations={st}&{Period}");
        var totals = report!["totals"]!.AsArray();
        Assert.Equal(1000m, totals.Single(t => t!["category"]!.GetValue<string>() == "По договору")!["amount"]!.GetValue<decimal>());
        Assert.Equal(500m, totals.Single(t => t!["category"]!.GetValue<string>() == "Топливные карты")!["amount"]!.GetValue<decimal>());
        var csv = await owner.GetStringAsync($"/api/v1/ui/analytics/contracts?stations={st}&{Period}&format=csv");
        Assert.StartsWith("﻿АЗС;Месяц;Способ оплаты", csv);
        Assert.Contains("\"ВЕДОМОСТИ\"", csv);

        // Settings: the guess, the cash register hint, an override that shows at once, and back to the guess.
        var modes = (await owner.GetFromJsonAsync<JsonArray>("/api/v1/ui/payment-modes"))!.Where(m => m!["station_id"]!.GetValue<Guid>() == st).ToList();
        Assert.Equal("contract", modes.Single(m => m!["payment_mode_id"]!.GetValue<long>() == 3)!["category"]!.GetValue<string>());
        Assert.Equal(1, modes.Single(m => m!["payment_mode_id"]!.GetValue<long>() == 3)!["paid_90d"]!.GetValue<long>());
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync($"/api/v1/ui/payment-modes/{st}/1", new { category = "contract" })).StatusCode);
        Assert.Equal(1500m, (await CurrentAsync())["contract"]!.GetValue<decimal>());
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync($"/api/v1/ui/payment-modes/{st}/1", JsonContent.Create(new JsonObject { ["category"] = null }))).StatusCode);
        Assert.Equal(500m, (await CurrentAsync())["cash"]!.GetValue<decimal>());
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/v1/ui/payment-modes/{st}/1", new { category = "barter" })).StatusCode);
        Assert.Equal(0, await fx.QueryAsync<long>("SELECT count(*) FROM payment_mode_settings WHERE station_id = @st", new { st }));

        // Another organization's owner cannot see or change them.
        var (_, stranger, _) = await StationAsync("АЗС-чужая");
        Assert.DoesNotContain(st.ToString(), await stranger.GetStringAsync("/api/v1/ui/payment-modes"));
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PutAsJsonAsync($"/api/v1/ui/payment-modes/{st}/1", new { category = "card" })).StatusCode);
    }

    [PostgresFact]
    public async Task Without_vat_contract_and_fuel_card_sales_lose_their_vat_too()
    {
        var (st, owner, _) = await StationAsync("АЗС-ндс-договоры");
        (await owner.PostAsJsonAsync("/api/v1/ui/costs/fuel", new { station_id = st, fuel_id = 1, valid_from = "2026-09-01T00:00:00+03:00", price_per_liter = 40 })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync("/api/v1/ui/organization/settings", new { margin_vat = "net", vat_rate = 20 })).StatusCode);
        await fx.WaitProcessedAsync();
        var fuel = (await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/analytics/finance?stations={st}&{Period}"))!["fuel"]!;
        // Paid 3 000 ₽ for 60 l at 40 ₽ → 600 ₽ before VAT. VAT base: receipted (500 cash + 500 "Наличные" by card)
        // + contract 1 000 + fuel card 500 = 2 500 ₽ → VAT ≈ 2 500 × 20 / 120, rounded per day and payment mode:
        // 83,33 + 83,33 + 166,67 + 83,33 = 416,66. The card sale without a receipt keeps its VAT.
        Assert.Equal(2500m, fuel["fiscal_revenue"]!.GetValue<decimal>());
        Assert.Equal(600m - 416.66m, fuel["margin"]!.GetValue<decimal>());
    }
}
