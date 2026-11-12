using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Topaz.Central.Ui;
using Topaz.Contracts;
using Xunit;

namespace Topaz.Central.Tests;

/// <summary>
/// 1.11: a station selling from two workplaces (АЗС «Дизель»): one shift, a local shift per workplace with its own
/// operator. Sales belong to the operator of their workplace, not to whoever opened the shift.
/// </summary>
public sealed class WorkplaceTests(CentralFixture fx) : IClassFixture<CentralFixture>
{
    private static string Hash(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    private static RecordChange Row(string dataset, string key, long? session, JsonObject data) =>
        new() { Dataset = dataset, Key = key, SessionId = session, Hash = Hash(dataset + key + data.ToJsonString()), Data = data };
    private static string Ts(DateTimeOffset t) => t.ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffzzz");

    private async Task SendAsync(string token, List<RecordChange> items)
    {
        var batch = new BatchEnvelope { BatchId = Guid.NewGuid(), Seq = 1, CreatedAt = DateTimeOffset.UtcNow, AgentVersion = "t", Items = items };
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true)) JsonSerializer.Serialize(gz, batch, Protocol.Json);
        var content = new ByteArrayContent(ms.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        (await fx.Client(token).PostAsync("/api/v1/batches", content)).EnsureSuccessStatusCode();
        await fx.WaitProcessedAsync();
    }

    [PostgresFact]
    public async Task Sales_belong_to_the_operator_of_their_workplace()
    {
        var org = await fx.QueryAsync<Guid>("SELECT id FROM organizations ORDER BY created_at, name LIMIT 1");
        var two = await fx.EnrollAsync("АЗС-два-места", org);
        var one = await fx.EnrollAsync("АЗС-одно-место", org);
        var t0 = DateTimeOffset.UtcNow.AddHours(-30);

        RecordChange Shift(int id, string user, bool closed = true) => Row("sysSessions", $"{id}", id, new JsonObject
        {
            ["SessionID"] = id, ["SessionNum"] = id, ["UserName"] = user, ["UserID"] = 1, ["Deleted"] = 0,
            ["StartDateTime"] = Ts(t0), ["EndDateTime"] = closed ? Ts(t0.AddHours(24)) : null,
        });
        RecordChange Local(int id, int session, int workbench, int user) => Row("sysLocalSessions", $"{id}", session, new JsonObject
        {
            ["LocalSessionID"] = id, ["SessionID"] = session, ["WorkbenchID"] = workbench, ["UserID"] = user, ["RegFault"] = 0,
            ["StartDateTime"] = Ts(t0.AddMinutes(workbench)), ["EndDateTime"] = Ts(t0.AddHours(24)),
        });
        var orderId = 0;
        RecordChange Order(int session, int? local, decimal volume, decimal amount)
        {
            orderId++;
            var data = new JsonObject
            {
                ["OrderID"] = orderId, ["SessionID"] = session, ["OrderNum"] = orderId, ["DateTime"] = Ts(t0.AddMinutes(10 + orderId)),
                ["FuelID"] = 1, ["TankID"] = 1, ["HoseID"] = 1, ["PaymentModeID"] = 1, ["Volume"] = volume, ["VolumeOrdered"] = volume,
                ["Price"] = 50, ["PricePump"] = 50, ["Amount"] = amount, ["AmountCash"] = amount, ["AmountCashless"] = 0, ["AmountDiscount"] = 0,
                ["Remark"] = "", ["StatusOrder"] = 0,
            };
            if (local is not null) data["LocalSessionID"] = local;
            return Row("flOrders", $"{orderId}", session, data);
        }

        var items = new List<RecordChange>
        {
            Row("dcPaymentModes", "1", null, new JsonObject { ["PaymentModeID"] = 1, ["Name"] = "Наличные" }),
            Row("sysWorkbenchs", "2", null, new JsonObject { ["WorkbenchID"] = 2, ["Name"] = "Основное рабочее место", ["ComputerName"] = "OPERATOR1", ["WorkbenchTypeID"] = 0, ["Deleted"] = 0 }),
            Row("sysWorkbenchs", "4", null, new JsonObject { ["WorkbenchID"] = 4, ["Name"] = "Дополнительное рабочее место", ["ComputerName"] = "OPERATOR2", ["WorkbenchTypeID"] = 3, ["Deleted"] = 0 }),
            Row("sysUsers", "110", null, new JsonObject { ["UserID"] = 110, ["Name"] = "Пользователь 1", ["Login"] = "Инна", ["Deleted"] = 0 }),
            Row("sysUsers", "112", null, new JsonObject { ["UserID"] = 112, ["Name"] = "Пользователь 1", ["Login"] = "Оля", ["Deleted"] = 0 }),
            // Inna opened the shift at the main workplace and sold one order; Olya sold at the additional one and aborted a lot.
            Shift(1, "Инна"), Local(11, 1, 2, 110), Local(12, 1, 4, 112),
            Order(1, 11, 20, 1000),
        };
        for (var i = 0; i < 6; i++) items.Add(Order(1, 12, 40, 2000));
        for (var i = 0; i < 6; i++) items.Add(Order(1, 12, 0, 0));
        // Shop receipt at the additional workplace.
        items.Add(Row("trFastTradeH", "1", 1, new JsonObject
        {
            ["FastTradeHID"] = 1, ["SessionID"] = 1, ["NDoc"] = 1, ["DateDoc"] = Ts(t0.AddHours(1)), ["IsReturn"] = 0, ["CashID"] = 3, ["PaymentModeID"] = 1,
            ["AmountCash"] = 300, ["AmountCashless"] = 0, ["Status"] = 0, ["FiscalNumber"] = 1, ["LocalSessionID"] = 12,
        }));
        items.Add(Row("trFastTradeD", "1", 1, new JsonObject { ["FastTradeDID"] = 1, ["FastTradeHID"] = 1, ["ItemID"] = 1, ["Quantity"] = 1, ["Price"] = 300, ["Amount"] = 300, ["Total"] = 300 }));
        await SendAsync(two.Token, items);

        // A station without local shifts (older data): everything stays with the shift's operator.
        orderId = 100;
        await SendAsync(one.Token, [Shift(1, "Дана"), Order(1, null, 10, 500), Order(1, null, 10, 500)]);

        var users = fx.Factory!.Services.GetRequiredService<UserService>();
        try { await users.CreateAsync("workplace-owner", "o", Roles.Owner, "workplace-password-1", [], org); } catch (PostgresException) { }
        var owner = fx.Factory.CreateClient();
        owner.DefaultRequestHeaders.Add(UiAuthSetup.CsrfHeader, "1");
        (await owner.PostAsJsonAsync("/api/v1/ui/auth/login", new { login = "workplace-owner", password = "workplace-password-1" })).EnsureSuccessStatusCode();

        var from = t0.AddDays(-1).ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd");
        var to = DateTimeOffset.UtcNow.AddDays(1).ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd");

        // Operators rating: Olya gets her 12 000 ₽ and her aborted orders, Inna only her 1 000 ₽; both worked one shift.
        var ops = (await owner.GetFromJsonAsync<JsonArray>($"/api/v1/ui/shifts/operators?stations={two.StationId}&from={from}&to={to}"))!;
        var olya = ops.Single(o => o!["user_name"]!.GetValue<string>() == "Оля")!;
        var inna = ops.Single(o => o!["user_name"]!.GetValue<string>() == "Инна")!;
        Assert.Equal(12000m, olya["revenue"]!.GetValue<decimal>());
        Assert.Equal(6, olya["aborted"]!.GetValue<long>());
        Assert.Equal(1, olya["shifts"]!.GetValue<long>());
        Assert.Equal(1000m, inna["revenue"]!.GetValue<decimal>());
        Assert.Equal(0, inna["aborted"]!.GetValue<long>());
        Assert.Equal(2, ops.Count);

        var single = (await owner.GetFromJsonAsync<JsonArray>($"/api/v1/ui/shifts/operators?stations={one.StationId}&from={from}&to={to}"))!;
        Assert.Equal("Дана", Assert.Single(single)!["user_name"]!.GetValue<string>());
        Assert.Equal(1000m, single[0]!["revenue"]!.GetValue<decimal>());

        // Shift list and card name both operators, main workplace first; the card splits the sales by workplace.
        var shifts = (await owner.GetFromJsonAsync<JsonArray>($"/api/v1/ui/shifts?stations={two.StationId}&from={from}&to={to}"))!;
        var shift = Assert.Single(shifts)!;
        Assert.Equal("Инна, Оля", shift["operators"]!.GetValue<string>());
        Assert.Equal(2, shift["workplaces"]!.GetValue<long>());
        var card = (await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/shifts/{two.StationId}/1"))!;
        Assert.Equal("Инна, Оля", card["header"]!["operators"]!.GetValue<string>());
        var places = card["workplaces"]!.AsArray();
        Assert.Equal(2, places.Count);
        Assert.Equal("основное", places[0]!["workplace"]!.GetValue<string>());
        Assert.Equal("Инна", places[0]!["operator"]!.GetValue<string>());
        Assert.Equal(1000m, places[0]!["revenue"]!.GetValue<decimal>());
        Assert.Equal("дополнительное", places[1]!["workplace"]!.GetValue<string>());
        Assert.Equal("Оля", places[1]!["operator"]!.GetValue<string>());
        Assert.Equal(12000m, places[1]!["revenue"]!.GetValue<decimal>());
        Assert.Equal(6, places[1]!["aborted"]!.GetValue<long>());
        Assert.Equal(300m, places[1]!["shop_amount"]!.GetValue<decimal>());
        Assert.Equal("Оля", card["orders"]!.AsArray().Single(o => o!["order_id"]!.GetValue<long>() == 2)!["operator"]!.GetValue<string>());

        var oneShift = Assert.Single((await owner.GetFromJsonAsync<JsonArray>($"/api/v1/ui/shifts?stations={one.StationId}&from={from}&to={to}"))!)!;
        Assert.Equal("Дана", oneShift["operators"]!.GetValue<string>());
        Assert.Equal(1, oneShift["workplaces"]!.GetValue<long>());

        // The aborted fuellings alert names the operator whose workplace aborted them.
        (await owner.PostAsync("/api/v1/ui/alerts/run", null)).EnsureSuccessStatusCode();
        var details = await fx.QueryAsync<string>("SELECT details FROM alerts WHERE station_id = @s AND rule_code = 'aborted_share'", new { s = two.StationId });
        Assert.Equal("Смена №1, оператор Оля (доп. место).", details);
    }
}
