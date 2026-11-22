using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Topaz.Central.Processing;
using Topaz.Central.Ui;
using Topaz.Contracts;
using Xunit;

namespace Topaz.Central.Tests;

/// <summary>
/// 1.23: shop purchasing — demand counted on the days an item was in stock, quantities to the next trip rounded to
/// packs, the owner's core / stop / min / max, shopping lists and their receipt through Topaz's incoming bills.
/// </summary>
public sealed class PurchaseTests(CentralFixture fx) : IClassFixture<CentralFixture>
{
    private static PurchasePlanner.Item Item(decimal soldInStock, int inStockDays, decimal stock = 0, decimal squares = -1) => new()
    {
        StationId = Guid.Empty, Station = "АЗС", ItemId = 1, ItemKey = "k", Name = "Вода", Stock = stock, SoldInStock = soldInStock,
        InStockDays = inStockDays, SoldSquares = squares >= 0 ? squares : inStockDays > 0 ? soldInStock * soldInStock / inStockDays : 0,
    };

    [Fact]
    public void Trips_are_the_next_trip_day_and_the_one_after_it()
    {
        var friday = new DateTime(2026, 11, 20);
        Assert.Equal((friday, new DateTime(2026, 11, 21)), PurchasePlanner.Trips(friday, [1, 2, 3, 4, 5, 6]));      // today is a trip day
        Assert.Equal((new DateTime(2026, 11, 23), new DateTime(2026, 11, 26)), PurchasePlanner.Trips(friday, [1, 4]));  // Monday, Thursday
        Assert.Equal((new DateTime(2026, 11, 22), new DateTime(2026, 11, 29)), PurchasePlanner.Trips(friday, [7]));     // once a week
    }

    [Fact]
    public void Service_levels_become_normal_quantiles()
    {
        Assert.Equal(0m, PurchasePlanner.Z(50));
        Assert.InRange(PurchasePlanner.Z(85), 1.03m, 1.04m);
        Assert.InRange(PurchasePlanner.Z(95), 1.64m, 1.65m);
        Assert.InRange(PurchasePlanner.Z(97), 1.88m, 1.89m);
    }

    [Fact]
    public void Lost_sales_count_the_days_out_of_stock_at_the_usual_demand()
    {
        ShopAvailability.Item Avail(string key, string mode, decimal sold, int days, decimal longSold, int longDays, int longAll, int outDays, decimal revenue) => new()
        {
            StationId = Guid.Empty, Station = "АЗС", ItemKey = key, Name = key, Mode = mode, SoldInStock = sold, InStockDays = days,
            LongSoldInStock = longSold, LongInStockDays = longDays, LongDays = longAll, Revenue = revenue,
            SoldAmount = longSold * 100, SoldQty = longSold, PeriodDays = 30, OutDays = outDays,
        };
        var (stations, items) = ShopAvailability.Compute(
        [
            Avail("water", "auto", 40, 20, 40, 20, 180, 10, 4000),   // 2 a day, out 10 days: 20 × 100 ₽
            Avail("burst", "auto", 0, 0, 10, 2, 180, 30, 0),         // 10 sold in 2 days half a year ago: 10 / 180 a day, not 5
            Avail("gum", "auto", 14, 14, 14, 14, 180, 0, 1400),
        ]);
        Assert.Equal(2000m, items.Single(i => i.ItemKey == "water").LostRevenue);
        Assert.Equal(166.67m, items.Single(i => i.ItemKey == "burst").LostRevenue);
        var st = Assert.Single(stations);
        Assert.False(st.CoreMarked);
        Assert.Equal(2, st.CoreItems);                               // class A by revenue: water and gum
        Assert.Equal(1m - 10m / 60, st.Availability!.Value, 4);
        Assert.Equal(2, st.OutItems);

        // Once the owner marks the core, only marked items count.
        (stations, _) = ShopAvailability.Compute([Avail("water", "auto", 40, 20, 40, 20, 180, 10, 4000), Avail("gum", "core", 14, 14, 14, 14, 180, 0, 1400)]);
        Assert.Equal(1m, stations.Single().Availability);
        Assert.True(stations.Single().CoreMarked);
    }

    [Fact]
    public void Days_on_zero_do_not_lower_the_demand()
    {
        // 24 sold in the 12 days it was there (then 44 days on zero): 2 a day, not 24 / 56.
        var line = PurchasePlanner.Compute(Item(24, 12), "A", PurchasePlanner.Settings.Default, 3);
        Assert.Equal(2m, line.PerDay);
        Assert.Equal(0m, line.Safety);          // the same every day: no variation
        Assert.Equal(6m, line.Qty);             // 3 days × 2
        Assert.Equal("out", line.Status);
        Assert.True(line.LowData);              // under 14 days in stock
    }

    [Fact]
    public void An_item_sold_out_long_ago_keeps_its_demand_from_the_long_window()
    {
        // Nothing on the shelf for the last 56 days; before that 3 a day for 40 days.
        var gone = new PurchasePlanner.Item { ItemKey = "k", SoldInStock = 0, InStockDays = 0, ZeroDays = 56, LongSoldInStock = 120, LongSoldSquares = 360, LongInStockDays = 40 };
        var line = PurchasePlanner.Compute(gone, "B", PurchasePlanner.Settings.Default, 2);
        Assert.Equal(3m, line.PerDay);
        Assert.Equal(6m, line.Qty);
        Assert.Equal("out", line.Status);
        Assert.Equal(40, line.InStockDays);
        // Enough recent days: the recent demand wins.
        gone.SoldInStock = 20; gone.SoldSquares = 40; gone.InStockDays = 10;
        Assert.Equal(2m, PurchasePlanner.Compute(gone, "B", PurchasePlanner.Settings.Default, 2).PerDay);
    }

    [Fact]
    public void Quantities_follow_packs_stock_orders_and_the_owners_plan()
    {
        var s = PurchasePlanner.Settings.Default;
        var basic = Item(60, 30, stock: 5);                                   // 2 a day, 5 on the shelf
        Assert.Equal(12m, PurchasePlanner.Compute(new() { SoldInStock = 60, InStockDays = 30, SoldSquares = 120, Stock = 5, PackGuess = 12, ItemKey = "k" }, "C", s, 7).Qty); // need 9 → one pack
        Assert.Equal(9m, PurchasePlanner.Compute(basic, "C", s, 7).Qty);
        basic.OnOrder = 6;
        Assert.Equal(3m, PurchasePlanner.Compute(basic, "C", s, 7).Qty);       // bought on the last trip, not received yet
        basic.OnOrder = 0;
        basic.MaxQty = 10;
        Assert.Equal(5m, PurchasePlanner.Compute(basic, "C", s, 7).Qty);       // up to the maximum
        basic.MaxQty = null;
        basic.Mode = "stop";
        Assert.Equal(0m, PurchasePlanner.Compute(basic, "C", s, 7).Qty);
        Assert.Equal("stop", PurchasePlanner.Compute(basic, "C", s, 7).Status);

        var unsold = Item(0, 0);
        Assert.Equal(0m, PurchasePlanner.Compute(unsold, "C", s, 7).Qty);
        unsold.Mode = "core";
        unsold.PackQty = 6;
        Assert.Equal(6m, PurchasePlanner.Compute(unsold, "C", s, 7).Qty);      // always in stock: at least one pack
        Assert.Equal("out", PurchasePlanner.Compute(unsold, "C", s, 7).Status);
        unsold.Mode = "auto";
        unsold.MinQty = 4;
        Assert.Equal(6m, PurchasePlanner.Compute(unsold, "C", s, 7).Qty);      // the minimum, in whole packs

        // Sales that vary need a safety stock, larger for A than for C.
        var varying = Item(60, 30, squares: 30 * (2 * 2 + 2 * 2));           // mean 2, σ = 2: E[x²] = σ² + mean² = 8
        Assert.True(PurchasePlanner.Compute(varying, "A", s, 4).Safety > PurchasePlanner.Compute(varying, "C", s, 4).Safety);
        Assert.Equal(Math.Round(PurchasePlanner.Z(97) * 2 * 2, 2), PurchasePlanner.Compute(varying, "A", s, 4).Safety);
        // Weight goods without a pack: to 0.1 kg.
        Assert.Equal(2.7m, PurchasePlanner.Compute(new() { SoldInStock = 4m, InStockDays = 3, SoldSquares = 16m / 3m, IsWeight = true, ItemKey = "k" }, "C", s, 2).Qty); // 1,333 kg a day × 2
    }

    // ---------------------------------------------------------------- integration

    private const string Password = "purchase-password-1";
    private static string Hash(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    private static RecordChange Row(string dataset, string key, JsonObject data) => new() { Dataset = dataset, Key = key, Hash = Hash(dataset + key + data.ToJsonString()), Data = data };
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

    private async Task<HttpClient> LoginAsync(string login, string role, Guid? org, params Guid[] stations)
    {
        var users = fx.Factory!.Services.GetRequiredService<UserService>();
        try { await users.CreateAsync(login, login, role, Password, stations, org); } catch (PostgresException) { /* exists */ }
        var c = fx.Factory.CreateClient();
        c.DefaultRequestHeaders.Add(UiAuthSetup.CsrfHeader, "1");
        (await c.PostAsJsonAsync("/api/v1/ui/auth/login", new { login, password = Password })).EnsureSuccessStatusCode();
        return c;
    }

    [PostgresFact]
    public async Task A_shopping_list_is_planned_bought_and_received()
    {
        var super = await LoginAsync("purchase-super", Roles.Superadmin, null);
        var created = await super.PostAsJsonAsync("/api/v1/ui/organizations", new { name = "Орг закупки" });
        created.EnsureSuccessStatusCode();
        var org = (await created.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        var st = await fx.EnrollAsync("АЗС-закупка", org);
        // Anchored to the Moscow date (the station's), not the UTC one: after 21:00 UTC they differ by a day.
        var today = DateTime.UtcNow.AddHours(3).Date;
        var day0 = new DateTimeOffset(DateTime.SpecifyKind(today, DateTimeKind.Unspecified), TimeSpan.FromHours(3)).AddHours(12);   // 12:00 Moscow
        int move = 0, receipt = 0;
        RecordChange Move(int item, DateTimeOffset at, decimal qty, int docType, int doc) => Row("rgItemRests", $"{++move}", new JsonObject
        {
            ["rgItemRestID"] = move, ["ItemID"] = item, ["StorageID"] = 1, ["DocTypeID"] = docType, ["DocID"] = doc, ["Date"] = Ts(at), ["Quantity"] = qty,
        });
        var items = new List<RecordChange>
        {
            Row("dcItemsGrps", "1", new JsonObject { ["ItemsGrpID"] = 1, ["Name"] = "Напитки" }),
            Row("dcItems", "1", new JsonObject { ["ItemID"] = 1, ["Name"] = "Вода 0,5", ["ItemCode"] = "W05", ["ItemsGrpID"] = 1 }),
            Row("dcItems", "2", new JsonObject { ["ItemID"] = 2, ["Name"] = "Чипсы", ["ItemCode"] = "CH1", ["ItemsGrpID"] = 1 }),
            Row("dcItems", "3", new JsonObject { ["ItemID"] = 3, ["Name"] = "Жвачка", ["ItemCode"] = "GM1", ["ItemsGrpID"] = 1 }),
            // Two bills of 12 bottles a month ago (the pack is learned from them), at 30 ₽.
            Row("trInBillH", "1", new JsonObject { ["InBillHID"] = 1, ["NDoc"] = "1", ["DateDoc"] = Ts(day0.AddDays(-31)), ["PartnerID"] = 1, ["StorageID"] = 1, ["IsReturn"] = 0, ["WithinNDS"] = 1 }),
            Row("trInBillD", "1", new JsonObject { ["InBillDID"] = 1, ["InBillHID"] = 1, ["ItemID"] = 1, ["ItemUnitID"] = 1, ["Quantity"] = 12, ["Price"] = 30, ["Total"] = 360 }),
            Row("trInBillH", "2", new JsonObject { ["InBillHID"] = 2, ["NDoc"] = "2", ["DateDoc"] = Ts(day0.AddDays(-31)), ["PartnerID"] = 1, ["StorageID"] = 1, ["IsReturn"] = 0, ["WithinNDS"] = 1 }),
            Row("trInBillD", "2", new JsonObject { ["InBillDID"] = 2, ["InBillHID"] = 2, ["ItemID"] = 1, ["ItemUnitID"] = 1, ["Quantity"] = 12, ["Price"] = 30, ["Total"] = 360 }),
            Move(1, day0.AddDays(-31), 12, 1, 1), Move(1, day0.AddDays(-31), 12, 1, 2),
            Move(3, day0.AddDays(-31), 10, 1, 1),
        };
        // Water: 2 a day for 12 days, then sold out for 18 days. Gum: 1 a day (marked "do not buy" below).
        for (var d = -30; d <= -19; d++)
        {
            foreach (var (item, qty) in new[] { (1, 2m), (3, 1m) })
            {
                receipt++;
                items.Add(Row("trFastTradeH", $"{receipt}", new JsonObject
                {
                    ["FastTradeHID"] = receipt, ["SessionID"] = 1, ["NDoc"] = receipt, ["DateDoc"] = Ts(day0.AddDays(d)), ["IsReturn"] = 0, ["CashID"] = 1,
                    ["PaymentModeID"] = 1, ["AmountCash"] = 0, ["AmountCashless"] = 0, ["Status"] = 0, ["FiscalNumber"] = 1,
                }));
                items.Add(Row("trFastTradeD", $"{receipt}", new JsonObject
                {
                    ["FastTradeDID"] = receipt, ["FastTradeHID"] = receipt, ["ItemID"] = item, ["Quantity"] = qty, ["Price"] = 50, ["Amount"] = 50 * qty, ["Total"] = 50 * qty,
                }));
                items.Add(Move(item, day0.AddDays(d), -qty, 5, receipt));
            }
        }
        await SendAsync(st.Token, items);

        var owner = await LoginAsync("purchase-owner", Roles.Owner, org);
        // Trips every day: the list must last until tomorrow's trip.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync("/api/v1/ui/purchasing/settings",
            new { trip_weekdays = new[] { 1, 2, 3, 4, 5, 6, 7 }, lead_days = 0, service_a = 97, service_b = 93, service_c = 85, history_days = 56 })).StatusCode);
        // Over the last 30 days both items were on the shelf 12 days and out 18; nothing is marked "always in stock" yet,
        // so the core is class A by revenue (both). Lost: 2 waters a day × 50 ₽ × 18 days + 1 gum × 50 ₽ × 18 days.
        var avail = (await owner.GetFromJsonAsync<JsonObject>(
            $"/api/v1/ui/analytics/availability?stations={st.StationId}&from={today.AddDays(-30):yyyy-MM-dd}&to={today:yyyy-MM-dd}"))!;
        Assert.Equal(0.4m, avail["total"]!["availability"]!.GetValue<decimal>());
        Assert.Equal(2, avail["total"]!["core_items"]!.GetValue<int>());
        Assert.False(avail["total"]!["core_marked"]!.GetValue<bool>());
        Assert.Equal(2700m, avail["total"]!["lost_revenue"]!.GetValue<decimal>());
        var lostWater = avail["items"]!.AsArray().First()!;
        Assert.Equal("W05", lostWater["item_key"]!.GetValue<string>());
        Assert.Equal(30, lostWater["period_days"]!.GetValue<int>());      // today is not counted: it is not over
        Assert.Equal(18, lostWater["out_days"]!.GetValue<int>());
        Assert.Equal(36m, lostWater["lost_qty"]!.GetValue<decimal>());
        Assert.Equal(1800m, lostWater["lost_revenue"]!.GetValue<decimal>());
        var viewerOfAvail = await LoginAsync("purchase-avail-viewer", Roles.Viewer, org, st.StationId);
        Assert.Equal(HttpStatusCode.OK, (await viewerOfAvail.GetAsync($"/api/v1/ui/analytics/availability?stations={st.StationId}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync("/api/v1/ui/purchasing/items",
            new { station_id = st.StationId, item_key = "CH1", mode = "core", pack_qty = 6 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync("/api/v1/ui/purchasing/items",
            new { station_id = st.StationId, item_key = "GM1", mode = "stop" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/v1/ui/purchasing/items",
            new { station_id = st.StationId, item_key = "GM1", mode = "auto", min_qty = 5, max_qty = 2 })).StatusCode);

        async Task<JsonObject> PlanAsync() => (await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/purchasing/plan?stations={st.StationId}"))!;
        JsonNode Line(JsonObject plan, string key) => plan["lines"]!.AsArray().Single(l => l!["item_key"]!.GetValue<string>() == key)!;
        var plan = await PlanAsync();
        Assert.Equal(1, plan["horizon_days"]!.GetValue<int>());
        var water = Line(plan, "W05");
        Assert.Equal(2m, water["per_day"]!.GetValue<decimal>());          // 24 sold in the 12 days it was there
        Assert.Equal(12, water["in_stock_days"]!.GetValue<int>());
        Assert.Equal(44, water["zero_days"]!.GetValue<int>());            // the other 44 of the 56 days
        Assert.Equal(12m, water["pack"]!.GetValue<decimal>());
        Assert.True(water["pack_learned"]!.GetValue<bool>());
        Assert.Equal(12m, water["qty"]!.GetValue<decimal>());             // need 2, one pack
        Assert.Equal(360m, water["amount"]!.GetValue<decimal>());         // at the last bill's price
        Assert.Equal("out", water["status"]!.GetValue<string>());
        Assert.Equal(6m, Line(plan, "CH1")["qty"]!.GetValue<decimal>());  // core, never sold: one pack
        Assert.Equal(0m, Line(plan, "GM1")["qty"]!.GetValue<decimal>());  // do not buy
        Assert.Equal(2, plan["totals"]!["items"]!.GetValue<int>());

        // A trip day (every day here) from hour 0: «Закупка сегодня» once, without money (viewers see alerts too).
        var detector = fx.Factory!.Services.GetRequiredService<AnomalyDetector>();
        await fx.QueryAsync<int>("INSERT INTO alert_rule_overrides (organization_id, code, threshold) VALUES (@org, 'shop_purchase_due', 0) RETURNING 1", new { org });
        await detector.RunAsync(CancellationToken.None);
        await detector.RunAsync(CancellationToken.None);
        Task<string> DueAsync() => fx.QueryAsync<string>(
            "SELECT string_agg(status || ': ' || title || ' | ' || details, ';') FROM alerts WHERE rule_code = 'shop_purchase_due' AND station_id = @s",
            new { s = st.StationId });
        Assert.Equal("new: Закупка сегодня: 2 поз. | Нет на полке: 2. Срочно («всегда в наличии»): Чипсы. Список — «Склад → Закупка».", await DueAsync());

        var csv = await owner.GetStringAsync($"/api/v1/ui/purchasing/plan?stations={st.StationId}&format=csv");
        Assert.StartsWith("﻿Группа;Товар;\"АЗС-закупка\";Всего;Цена;Сумма", csv);
        Assert.Contains("\"Напитки\";\"Вода 0,5\";12;12;30,00;360,00", csv);

        // Save, buy: the water is on order and is not suggested again.
        var tripDate = DateTime.UtcNow.Date;
        var lines = new[]
        {
            new { station_id = st.StationId, item_id = 1L, item_key = "W05", name = "Вода 0,5", group_name = "Напитки", suggested_qty = 12m, qty = 12m, price = (decimal?)30m },
            new { station_id = st.StationId, item_id = 2L, item_key = "CH1", name = "Чипсы", group_name = "Напитки", suggested_qty = 6m, qty = 6m, price = (decimal?)null },
        };
        var saved = await owner.PostAsJsonAsync("/api/v1/ui/purchasing/lists", new { trip_date = tripDate, lines });
        saved.EnsureSuccessStatusCode();
        var id = (await saved.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<long>();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/v1/ui/purchasing/lists/{id}/bought", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync($"/api/v1/ui/purchasing/lists/{id}", new { trip_date = tripDate, lines })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync($"/api/v1/ui/purchasing/lists/{id}")).StatusCode);
        plan = await PlanAsync();
        Assert.Equal(12m, Line(plan, "W05")["on_order"]!.GetValue<decimal>());
        Assert.Equal(0m, Line(plan, "W05")["qty"]!.GetValue<decimal>());

        await detector.RunAsync(CancellationToken.None);
        Assert.StartsWith("closed:", await DueAsync());                         // bought today

        // Bought three days ago and nothing received: an alert in «Контроль».
        await fx.QueryAsync<int>("UPDATE purchase_lists SET bought_at = now() - interval '3 days' WHERE id = @id RETURNING 1", new { id });
        await detector.RunAsync(CancellationToken.None);
        Task<string> AlertAsync() => fx.QueryAsync<string>(
            "SELECT status || ': ' || title FROM alerts WHERE rule_code = 'shop_purchase_not_received' AND station_id = @s AND dedupe_key = @k",
            new { s = st.StationId, k = id.ToString() });
        Assert.Equal("new: Куплено, но не оприходовано в Топазе: 2 поз.", await AlertAsync());

        // Topaz receives the water today: the line is received, nothing is on order.
        await SendAsync(st.Token,
        [
            Row("trInBillH", "3", new JsonObject { ["InBillHID"] = 3, ["NDoc"] = "3", ["DateDoc"] = Ts(DateTimeOffset.UtcNow), ["PartnerID"] = 1, ["StorageID"] = 1, ["IsReturn"] = 0, ["WithinNDS"] = 1 }),
            Row("trInBillD", "3", new JsonObject { ["InBillDID"] = 3, ["InBillHID"] = 3, ["ItemID"] = 1, ["ItemUnitID"] = 1, ["Quantity"] = 12, ["Price"] = 31, ["Total"] = 372 }),
            Move(1, DateTimeOffset.UtcNow, 12, 1, 3),
        ]);
        var card = (await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/purchasing/lists/{id}"))!;
        var receivedWater = card["lines"]!.AsArray().Single(l => l!["item_key"]!.GetValue<string>() == "W05")!;
        Assert.Equal(12m, receivedWater["received_qty"]!.GetValue<decimal>());
        Assert.Equal(0m, card["lines"]!.AsArray().Single(l => l!["item_key"]!.GetValue<string>() == "CH1")!["received_qty"]!.GetValue<decimal>());
        plan = await PlanAsync();
        Assert.Equal(0m, Line(plan, "W05")["on_order"]!.GetValue<decimal>());
        Assert.Equal(12m, Line(plan, "W05")["stock"]!.GetValue<decimal>());
        Assert.Equal(31m, Line(plan, "W05")["price"]!.GetValue<decimal>());   // the newest bill
        var listed = Assert.Single((await owner.GetFromJsonAsync<JsonArray>("/api/v1/ui/purchasing/lists"))!)!;
        Assert.Equal("bought", listed["status"]!.GetValue<string>());
        Assert.Equal(1, listed["open_items"]!.GetValue<int>());                // the chips are still awaited
        await detector.RunAsync(CancellationToken.None);
        Assert.StartsWith("new:", await AlertAsync());                          // the chips are still awaited
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/v1/ui/purchasing/lists/{id}/close", null)).StatusCode);
        await detector.RunAsync(CancellationToken.None);
        Assert.StartsWith("closed:", await AlertAsync());
        Assert.Equal(0m, Line(await PlanAsync(), "CH1")["on_order"]!.GetValue<decimal>());

        // Water marked "always in stock" runs out: one alert, closed when it is back.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync("/api/v1/ui/purchasing/items",
            new { station_id = st.StationId, item_key = "W05", mode = "core" })).StatusCode);
        Task<string> CoreOutAsync() => fx.QueryAsync<string>(
            "SELECT COALESCE(string_agg(status || ': ' || title, ';'), '') FROM alerts WHERE rule_code = 'shop_core_out' AND station_id = @s", new { s = st.StationId });
        await detector.RunAsync(CancellationToken.None);
        Assert.Equal("", await CoreOutAsync());
        await SendAsync(st.Token, [Move(1, DateTimeOffset.UtcNow, -12, 5, 900)]);
        await detector.RunAsync(CancellationToken.None);
        await detector.RunAsync(CancellationToken.None);
        Assert.Equal("new: Закончился товар «всегда в наличии»: Вода 0,5", await CoreOutAsync());
        await SendAsync(st.Token, [Move(1, DateTimeOffset.UtcNow, 6, 1, 901)]);
        await detector.RunAsync(CancellationToken.None);
        Assert.Equal("closed: Закончился товар «всегда в наличии»: Вода 0,5", await CoreOutAsync());

        // A viewer sees no purchase prices; another organization's station cannot be planned or listed.
        var viewer = await LoginAsync("purchase-viewer", Roles.Viewer, org, st.StationId);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/ui/purchasing/plan")).StatusCode);
        var otherOrg = await super.PostAsJsonAsync("/api/v1/ui/organizations", new { name = "Орг закупки чужая" });
        otherOrg.EnsureSuccessStatusCode();
        var other = await fx.EnrollAsync("АЗС-закупка-чужая", (await otherOrg.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>());
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v1/ui/purchasing/plan?stations={other.StationId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PutAsJsonAsync("/api/v1/ui/purchasing/items", new { station_id = other.StationId, item_key = "W05", mode = "core" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/ui/purchasing/lists",
            new { trip_date = tripDate, lines = new[] { new { station_id = other.StationId, item_id = 1L, item_key = "W05", suggested_qty = 1m, qty = 1m } } })).StatusCode);
    }
}
