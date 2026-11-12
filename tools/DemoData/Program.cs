using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Topaz.Contracts;

// Synthetic Topaz stations for developing and demonstrating the web interface.
// Rows use real Topaz table/column names and go through the real agent protocol (/enroll + /batches).
//
//   dotnet run --project tools/DemoData -- --server http://localhost:8080 --admin-token ADMIN_TOKEN [--stations 5] [--days 180] [--seed 1] [--scale 1.0]
//
// Built-in anomalies for the "Контроль" page: a hose counter gap between two shifts, a negative fuel receipt
// discrepancy, an operator with many aborted fuellings, a never-selling item, items that run out of stock.

string Arg(string name, string fallback) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
var server = Arg("--server", "http://localhost:8080").TrimEnd('/');
var key = Arg("--admin-token", Environment.GetEnvironmentVariable("TOPAZ_ADMIN_TOKEN") ?? ""); // approves the demo stations' connection requests
var stationCount = int.Parse(Arg("--stations", "5"), CultureInfo.InvariantCulture);
var days = int.Parse(Arg("--days", "180"), CultureInfo.InvariantCulture);
var seed = int.Parse(Arg("--seed", "1"), CultureInfo.InvariantCulture);
var scale = double.Parse(Arg("--scale", "1.0"), CultureInfo.InvariantCulture);
var prefix = Arg("--prefix", "Демо");

var profiles = new[]
{
    new Profile($"{prefix} АЗС-1 Центр", "Europe/Moscow", City: true, [("АИ-92", 52.9m, 2600), ("АИ-95", 58.4m, 2100), ("ДТ", 69.1m, 900)], ShopRate: 0.30, CashlessShare: 0.68),
    new Profile($"{prefix} АЗС-2 Трасса М-4", "Europe/Moscow", City: false, [("АИ-92", 53.4m, 1800), ("АИ-95", 59.0m, 1300), ("ДТ", 69.8m, 3400), ("СУГ", 28.9m, 700)], ShopRate: 0.38, CashlessShare: 0.74),
    new Profile($"{prefix} АЗС-3 Газовая", "Europe/Samara", City: true, [("СУГ", 27.5m, 2300), ("АИ-92", 52.1m, 900)], ShopRate: 0.12, CashlessShare: 0.45),
    new Profile($"{prefix} АЗС-4 Северная", "Asia/Yekaterinburg", City: true, [("АИ-92", 51.8m, 2200), ("АИ-95", 57.2m, 1700), ("АИ-100", 71.5m, 250), ("ДТ", 67.9m, 1100)], ShopRate: 0.26, CashlessShare: 0.71),
    new Profile($"{prefix} АЗС-5 Пригород", "Europe/Moscow", City: true, [("АИ-92", 52.6m, 1500), ("АИ-95", 58.1m, 900), ("ДТ", 68.7m, 600)], ShopRate: 0.22, CashlessShare: 0.55),
};

using var http = new HttpClient { BaseAddress = new Uri(server + "/"), Timeout = TimeSpan.FromMinutes(5) };
var end = DateTime.Today.AddDays(1).AddHours(8); // last shift is the current (open) one
for (var s = 0; s < Math.Min(stationCount, profiles.Length * 4); s++)
{
    var p = profiles[s % profiles.Length];
    if (s >= profiles.Length) p = p with { Name = $"{p.Name} ({s / profiles.Length + 1})" };
    var gen = new StationGenerator(p, new Random(seed * 1000 + s), scale, anomalies: s == 1);
    var rows = gen.Generate(end.AddDays(-days), end);
    Console.WriteLine($"{p.Name}: {rows.Count:N0} rows ({gen.Orders:N0} fuel orders, {gen.ShopReceipts:N0} shop receipts)");
    await Sender.SendAsync(http, key, p.Name, p.TimeZone, rows);
}
Console.WriteLine("Done. The server processes batches in the background; check /api/v1/admin/stations.");

internal sealed record Profile(string Name, string TimeZone, bool City, (string Fuel, decimal Price, int LitersPerDay)[] Fuels, double ShopRate, double CashlessShare);

internal sealed class StationGenerator(Profile p, Random rnd, double scale, bool anomalies)
{
    private readonly TimeZoneInfo _tz = TimeZoneInfo.FindSystemTimeZoneById(p.TimeZone);
    private readonly List<RecordChange> _rows = new();
    public int Orders { get; private set; }
    public int ShopReceipts { get; private set; }

    private static readonly string[] Operators = ["Иванова А.", "Петров С.", "Смирнова Е.", "Кузнецов Д.", "Волкова М."];
    private static readonly (string Group, string Name, string Code, decimal Price, double Weight)[] Catalog =
    [
        ("Кофе", "Кофе американо 0,3", "", 150, 30), ("Кофе", "Кофе капучино 0,4", "", 190, 26), ("Кофе", "Кофе латте 0,4", "", 200, 18),
        ("Кофе", "Чай чёрный", "", 80, 8), ("Кофе", "Какао", "", 160, 4),
        ("Напитки", "Вода питьевая 0,5", "4600433600325", 55, 22), ("Напитки", "Вода газ. 0,5", "4600433600332", 60, 9),
        ("Напитки", "Энергетик Адреналин 0,449", "4600494694202", 145, 16), ("Напитки", "Энергетик Red Bull 0,25", "9002490100070", 170, 9),
        ("Напитки", "Кока-кола 0,5", "5449000000439", 110, 12), ("Напитки", "Сок апельсиновый 0,2", "4607025392040", 70, 4),
        ("Снеки", "Шоколад Snickers", "5000159461122", 85, 14), ("Снеки", "Батончик Twix", "5000159459228", 85, 7),
        ("Снеки", "Чипсы Lays 70г", "4690388031534", 120, 6), ("Снеки", "Хот-дог классический", "", 180, 15),
        ("Снеки", "Сэндвич с курицей", "", 210, 6), ("Снеки", "Жевательная резинка Orbit", "4009900538939", 55, 10),
        ("Масла", "Масло моторное 5W-40 1л", "4607071451234", 790, 3), ("Масла", "Масло моторное 5W-40 4л", "4607071451241", 2650, 1.2),
        ("Масла", "Масло моторное 10W-40 4л", "4607071459872", 1990, 0.8),
        ("Автотовары", "Омыватель зимний 4л", "4607072190002", 420, 5), ("Автотовары", "Омыватель летний 4л", "4607072190019", 290, 2),
        ("Автотовары", "Антифриз G12 1кг", "4607075783461", 450, 1.2), ("Автотовары", "Щётка-скребок", "4627131420098", 390, 1),
        ("Автотовары", "Салфетки влажные для стёкол", "4607072194536", 150, 2.5), ("Автотовары", "Освежитель воздуха", "4602064000012", 250, 1.6),
        ("Автотовары", "Предохранители набор", "4620002410014", 180, 0.3), ("Автотовары", "Лампа H7", "4008321014613", 690, 0.4),
        ("Автотовары", "Трос буксировочный", "4610024550017", 990, 0.15), ("Автотовары", "Набор автомобилиста (не продаётся)", "4610024559999", 3500, 0),
    ];

    // The highway station sells from two workplaces (main + additional), like АЗС «Дизель»; sometimes with two operators.
    private readonly bool _twoPlaces = p.Name.Contains("Трасса", StringComparison.Ordinal);
    private int _lsMain, _lsExtra;
    private int Local() => _twoPlaces && rnd.NextDouble() < 0.55 ? _lsExtra : _lsMain;

    private int _orderId, _sessionId, _ftH, _ftD, _rgItem, _rgCash, _receiptId, _eventId, _billH, _billD;

    public List<RecordChange> Generate(DateTime from, DateTime to)
    {
        // ---- references
        Ref("dcPaymentModes", "0", new() { ["PaymentModeID"] = 0, ["Name"] = "Переливы" });
        Ref("dcPaymentModes", "1", new() { ["PaymentModeID"] = 1, ["Name"] = "Наличные" });
        Ref("dcPaymentModes", "2", new() { ["PaymentModeID"] = 2, ["Name"] = "Терминал Сбербанк" });
        Ref("dcPaymentModes", "3", new() { ["PaymentModeID"] = 3, ["Name"] = "Тех. прокачка" });
        Ref("dcCashes", "0", new() { ["CashID"] = 0, ["Name"] = " <Не выбран>" });
        Ref("dcCashes", "1", new() { ["CashID"] = 1, ["Name"] = "ФР Штрих-М он-лайн (ФЗ 54)" });
        Ref("dcStorages", "1", new() { ["StorageID"] = 1, ["Name"] = "Розничный склад" });
        Ref("dcPartners", "1", new() { ["PartnerID"] = 1, ["Name"] = "Частное лицо" });
        Ref("dcAgents", "0", new() { ["AgentID"] = 0, ["Name"] = " <Не выбрано>", ["PartnerID"] = 0 });
        foreach (var (id, name) in new[] { (1, "Приходная накладная"), (3, "Ввод остатков товаров"), (5, "Чек быстрой продажи"), (6, "Отпуск топлива"), (7, "Прием топлива"), (14, "Внесение наличных"), (15, "Выдача наличных") })
            Ref("sysDocTypes", $"{id}", new() { ["DocTypeID"] = id, ["Name"] = name });
        Ref("sysWorkbenchs", "2", new() { ["WorkbenchID"] = 2, ["Name"] = "Основное рабочее место", ["ComputerName"] = "OPERATOR1", ["WorkbenchTypeID"] = 0, ["Deleted"] = 0 });
        if (_twoPlaces)
            Ref("sysWorkbenchs", "3", new() { ["WorkbenchID"] = 3, ["Name"] = "Дополнительное рабочее место", ["ComputerName"] = "OPERATOR2", ["WorkbenchTypeID"] = 3, ["Deleted"] = 0 });
        for (var i = 0; i < Operators.Length; i++)
            Ref("sysUsers", $"{i + 101}", new() { ["UserID"] = i + 101, ["Name"] = "Пользователь 1", ["Login"] = Operators[i], ["Deleted"] = 0 });
        Ref("sysEventCategores", "1", new() { ["EventCategoryID"] = 1, ["EventCategoryName"] = "Смена" });
        Ref("sysEventCategores", "2", new() { ["EventCategoryID"] = 2, ["EventCategoryName"] = "Приём топлива" });

        var fuels = p.Fuels.Select((f, i) => new FuelState(i + 1, f.Fuel, f.Price, f.LitersPerDay * scale, f.Fuel == "СУГ" ? 10000m : f.Fuel == "АИ-100" ? 5000m : 25000m)).ToList();
        var pumps = Math.Max(2, (int)Math.Ceiling(fuels.Sum(f => f.LitersPerDay) / 2500));
        var hoseId = 0;
        foreach (var f in fuels)
        {
            Ref("dcFuels", $"{f.Id}", new() { ["FuelID"] = f.Id, ["Name"] = f.Name, ["FullName"] = f.Name, ["Price"] = f.Price, ["Deleted"] = 0 });
            Ref("dcTanks", $"{f.Id}", new() { ["TankID"] = f.Id, ["Num"] = f.Id, ["FuelID"] = f.Id, ["Name"] = $"Р{f.Id} {f.Name}", ["MaxVolume"] = f.Capacity, ["DeadRestLiter"] = f.Capacity * 0.03m, ["DeadRest"] = 40, ["Deleted"] = 0 });
            f.Stock = f.Capacity * 0.75m;
        }
        for (var pump = 1; pump <= pumps; pump++)
        {
            Ref("dcPumps", $"{pump}", new() { ["PumpID"] = pump, ["Num"] = pump, ["Deleted"] = 0 });
            for (var i = 0; i < fuels.Count; i++)
            {
                hoseId++;
                var f = fuels[i];
                f.Hoses.Add(new HoseState(hoseId, pump, i + 1, rnd.Next(10_000, 900_000)));
                Ref("dcHoses", $"{hoseId}", new() { ["HoseID"] = hoseId, ["PumpID"] = pump, ["TankID"] = f.Id, ["Num"] = hoseId, ["NumInPump"] = i + 1, ["Deleted"] = 0 });
            }
        }
        var groups = Catalog.Select(c => c.Group).Distinct().ToList();
        foreach (var (g, i) in groups.Select((g, i) => (g, i)))
            Ref("dcItemsGrps", $"{-(i + 1)}", new() { ["ItemsGrpID"] = -(i + 1), ["Name"] = g, ["ParentID"] = 0 });
        var items = Catalog.Select((c, i) => new ItemState(i + 1, c.Name, c.Code, -(groups.IndexOf(c.Group) + 1), c.Price, c.Weight)).ToList();
        foreach (var it in items)
            Ref("dcItems", $"{it.Id}", new() { ["ItemID"] = it.Id, ["ItemsGrpID"] = it.Group, ["Name"] = it.Name, ["FullName"] = it.Name, ["ItemCode"] = it.Code, ["IsService"] = 0 });

        // ---- initial goods stock
        foreach (var it in items) Move(it, it.Weight == 0 ? 2 : Target(it), from, 3, 1);

        // ---- shifts: 24 h, 08:00 local
        var opIndex = rnd.Next(Operators.Length);
        var weekdayFactor = new[] { 0.94, 0.95, 0.98, 1.02, 1.16, 1.10, 0.85 };
        var day = 0;
        for (var start = from.Date.AddHours(8); start < to; start = start.AddDays(1), day++)
        {
            _sessionId++;
            var shiftEnd = start.AddDays(1);
            var open = shiftEnd > DateTime.Now;
            var user = Operators[(opIndex + day) % Operators.Length];
            var sessionEnd = open ? (DateTime?)null : shiftEnd.AddMinutes(-rnd.Next(2, 9));
            Add("sysSessions", $"{_sessionId}", _sessionId, new()
            {
                ["SessionID"] = _sessionId, ["SessionNum"] = _sessionId, ["UserName"] = user, ["UserID"] = 1,
                ["StartDateTime"] = Ts(start.AddMinutes(rnd.Next(0, 5))), ["EndDateTime"] = sessionEnd is null ? null : Ts(sessionEnd.Value), ["Deleted"] = 0,
            });
            Event(start, 1, $"Открытие смены №{_sessionId}. Оператор: {user}");
            // Local shifts of the workplaces; on a quarter of the days a second operator works at the additional one.
            var userIdx = (opIndex + day) % Operators.Length;
            _lsMain = _twoPlaces ? _sessionId * 2 : _sessionId;
            _lsExtra = _sessionId * 2 + 1;
            Add("sysLocalSessions", $"{_lsMain}", _sessionId, new()
            {
                ["LocalSessionID"] = _lsMain, ["SessionID"] = _sessionId, ["WorkbenchID"] = 2, ["UserID"] = userIdx + 101, ["RegFault"] = 0,
                ["StartDateTime"] = Ts(start.AddMinutes(5)), ["EndDateTime"] = sessionEnd is null ? null : Ts(sessionEnd.Value),
            });
            if (_twoPlaces)
                Add("sysLocalSessions", $"{_lsExtra}", _sessionId, new()
                {
                    ["LocalSessionID"] = _lsExtra, ["SessionID"] = _sessionId, ["WorkbenchID"] = 3, ["RegFault"] = 0,
                    ["UserID"] = (rnd.NextDouble() < 0.25 ? (userIdx + 1) % Operators.Length : userIdx) + 101,
                    ["StartDateTime"] = Ts(start.AddMinutes(8)), ["EndDateTime"] = sessionEnd is null ? null : Ts(sessionEnd.Value.AddMinutes(-1)),
                });

            // price changes (inflation, occasional discounting), weekly-ish chance
            foreach (var f in fuels)
                if (rnd.NextDouble() < 1 / 45.0)
                    f.Price = Math.Max(1, f.Price + (rnd.NextDouble() < 0.15 ? -0.4m : Math.Round((decimal)(0.2 + rnd.NextDouble() * 1.3), 1)));

            var cashFuel = 0m; var cashShop = 0m; var orderNum = 0;
            var startStock = fuels.ToDictionary(f => f.Id, f => f.Stock);
            var received = fuels.ToDictionary(f => f.Id, _ => 0m);
            var pumped = fuels.ToDictionary(f => f.Id, _ => 0m);
            var hoseStart = fuels.SelectMany(f => f.Hoses).ToDictionary(h => h.Id, h => h.Counter);
            var abortRate = anomalies && user == Operators[2] ? 0.09 : 0.022; // one operator aborts a lot at the anomaly station

            var season = 1 + 0.14 * Math.Sin((start.DayOfYear - 110) / 365.0 * 2 * Math.PI);
            var trend = 1 + 0.06 * day / 365.0;
            var dowF = weekdayFactor[((int)start.DayOfWeek + 6) % 7];
            var events = new List<(DateTime At, Action Act)>();
            foreach (var f in fuels)
            {
                var elasticity = Math.Pow((double)(f.BasePrice / f.Price), 1.4);
                var liters = f.LitersPerDay * season * trend * dowF * elasticity * LogNormal(0.10);
                var avgFill = f.Name == "ДТ" && !p.City ? 70.0 : f.Name == "СУГ" ? 32.0 : 31.0;
                var count = Poisson(liters / avgFill);
                for (var i = 0; i < count; i++)
                {
                    var at = start.AddMinutes(HourOfDay() * 60 + rnd.NextDouble() * 60);
                    if (at >= DateTime.Now) continue;
                    var fuel = f;
                    events.Add((at, () =>
                    {
                        var hose = fuel.Hoses[rnd.Next(fuel.Hoses.Count)];
                        var (vol, ordered, prepaid) = Fill(fuel, avgFill);
                        var aborted = rnd.NextDouble() < abortRate;
                        if (aborted) vol = 0;
                        var discountPerL = rnd.NextDouble() < 0.12 ? 1m : 0m;
                        var amount = Math.Round(vol * (fuel.Price - discountPerL), 2);
                        var cashless = rnd.NextDouble() < p.CashlessShare;
                        _orderId++; orderNum++; Orders++;
                        Add("flOrders", $"{_orderId}", _sessionId, new()
                        {
                            ["OrderID"] = _orderId, ["SessionID"] = _sessionId, ["OrderNum"] = orderNum, ["DateTime"] = Ts(at), ["TankID"] = fuel.Id, ["FuelID"] = fuel.Id,
                            ["HoseID"] = hose.Id, ["PaymentModeID"] = cashless ? 2 : 1, ["Remark"] = cashless ? "Б" : "", ["Volume"] = vol, ["VolumeOrdered"] = ordered,
                            ["Price"] = fuel.Price, ["PricePump"] = fuel.Price, ["Amount"] = amount, ["AmountCash"] = cashless ? 0 : (aborted ? 0 : prepaid ?? amount),
                            ["AmountCashless"] = cashless ? amount : 0, ["AmountDiscount"] = Math.Round(vol * discountPerL, 2), ["AmountBonuses"] = 0, ["AccruedBonuses"] = 0,
                            ["Mass"] = 0, ["Density"] = 0, ["StatusOrder"] = 0, ["OrderSource"] = -1, ["CardType"] = -1, ["CardCode"] = "", ["FiscalNumber"] = aborted ? 0 : _orderId,
                            ["DriverID"] = 0, ["AgentID"] = 0, ["TaxStr"] = "Б/НДС", ["LocalSessionID"] = Local(),
                        });
                        hose.Counter += vol; fuel.Stock -= vol; pumped[fuel.Id] += vol;
                        if (!cashless && amount > 0)
                        {
                            cashFuel += amount;
                            Cash(at, 6, _orderId, 1, amount);
                        }
                    }));
                }
                // technical pumping now and then
                if (rnd.NextDouble() < 0.08)
                {
                    var at = start.AddHours(rnd.Next(9, 18));
                    if (at < DateTime.Now)
                    {
                        var fuel = f;
                        events.Add((at, () =>
                        {
                            var hose = fuel.Hoses[0];
                            _orderId++; orderNum++;
                            Add("flOrders", $"{_orderId}", _sessionId, new()
                            {
                                ["OrderID"] = _orderId, ["SessionID"] = _sessionId, ["OrderNum"] = orderNum, ["DateTime"] = Ts(at), ["TankID"] = fuel.Id, ["FuelID"] = fuel.Id,
                                ["HoseID"] = hose.Id, ["PaymentModeID"] = 3, ["Remark"] = "", ["Volume"] = 10m, ["VolumeOrdered"] = 10m, ["Price"] = fuel.Price, ["PricePump"] = fuel.Price,
                                ["Amount"] = 0, ["AmountCash"] = 0, ["AmountCashless"] = 0, ["AmountDiscount"] = 0, ["AmountBonuses"] = 0, ["Mass"] = 0, ["Density"] = 0,
                                ["StatusOrder"] = 0, ["OrderSource"] = -1, ["CardType"] = -1, ["CardCode"] = "", ["TaxStr"] = "Б/НДС", ["LocalSessionID"] = Local(),
                            });
                            hose.Counter += 10; fuel.Stock -= 10; pumped[fuel.Id] += 10;
                        }));
                    }
                }
            }

            // shop
            var fuelOrders = events.Count;
            var receipts = Poisson(fuelOrders * p.ShopRate * LogNormal(0.1));
            for (var i = 0; i < receipts; i++)
            {
                var at = start.AddMinutes(HourOfDay() * 60 + rnd.NextDouble() * 60);
                if (at >= DateTime.Now) continue;
                events.Add((at, () =>
                {
                    var lines = new List<(ItemState It, decimal Qty)>();
                    var n = rnd.NextDouble() < 0.7 ? 1 : rnd.NextDouble() < 0.8 ? 2 : 3;
                    for (var k = 0; k < n; k++)
                    {
                        var it = PickItem(items);
                        if (it is null || it.Stock <= 0 || lines.Any(l => l.It == it)) continue;
                        lines.Add((it, it.Price > 500 ? 1 : rnd.NextDouble() < 0.85 ? 1 : 2));
                    }
                    if (lines.Count == 0) return;
                    var cashless = rnd.NextDouble() < p.CashlessShare * 0.85;
                    _ftH++; ShopReceipts++;
                    var total = lines.Sum(l => l.It.Price * l.Qty);
                    Add("trFastTradeH", $"{_ftH}", _sessionId, new()
                    {
                        ["FastTradeHID"] = _ftH, ["SessionID"] = _sessionId, ["NDoc"] = _ftH, ["DateDoc"] = Ts(at), ["IsReturn"] = 0, ["CashID"] = 1, ["StorageID"] = 1,
                        ["PartnerID"] = 1, ["PaymentModeID"] = cashless ? 2 : 1, ["AmountCash"] = cashless ? 0 : total, ["AmountCashless"] = cashless ? total : 0,
                        ["AmountDiscount"] = 0, ["AmountBonuses"] = 0, ["Status"] = 0, ["FiscalNumber"] = _ftH, ["LocalSessionID"] = Local(),
                    });
                    foreach (var (it, qty) in lines)
                    {
                        _ftD++;
                        Add("trFastTradeD", $"{_ftD}", _sessionId, new()
                        {
                            ["FastTradeDID"] = _ftD, ["FastTradeHID"] = _ftH, ["ItemID"] = it.Id, ["ItemUnitID"] = it.Id, ["Quantity"] = qty, ["Price"] = it.Price,
                            ["Amount"] = it.Price * qty, ["AmountDiscount"] = 0, ["NdsAmount"] = 0, ["Total"] = it.Price * qty,
                        });
                        Move(it, -qty, at, 5, _ftH);
                    }
                    if (!cashless) { cashShop += total; Cash(at, 5, _ftH, 1, total); }
                }));
            }

            // fuel deliveries when the book stock gets low
            foreach (var f in fuels)
            {
                var expected = (decimal)(f.LitersPerDay * season * dowF);
                if (f.Stock - expected < f.Capacity * 0.28m)
                {
                    var at = start.AddHours(rnd.Next(10, 16)).AddMinutes(rnd.Next(60));
                    if (at >= DateTime.Now) continue;
                    var fuel = f;
                    events.Add((at, () =>
                    {
                        var docVolume = Math.Round(fuel.Capacity * (decimal)(0.55 + rnd.NextDouble() * 0.15) / 10) * 10;
                        var disc = anomalies && fuel.Id == 3 && _receiptId % 4 == 1 ? -180m : Math.Round((decimal)(rnd.NextDouble() * 40 - 20), 0);
                        var measured = docVolume + disc;
                        _receiptId++;
                        Add("flRecepts", $"{_receiptId}", _sessionId, new()
                        {
                            ["ReceptID"] = _receiptId, ["SessionID"] = _sessionId, ["ReceptDateTime"] = Ts(at), ["TankID"] = fuel.Id, ["FuelID"] = fuel.Id, ["PartnerID"] = 1,
                            ["DocNumber"] = $"ТТН-{at:yyMMdd}-{_receiptId}", ["DocCarNumber"] = $"А{rnd.Next(100, 999)}ВС{rnd.Next(10, 99)}", ["DocVolume"] = docVolume,
                            ["MeasuredVolume"] = measured, ["Discrepancy"] = disc, ["DocMass"] = Math.Round(docVolume * 0.74m), ["DocDensity"] = 0.7400m, ["DocTemperature"] = 12.0m,
                            ["DocFuelPrice"] = 0, ["ReceptNumber"] = _receiptId, ["LocalSessionID"] = Local(),
                        });
                        fuel.Stock += measured; received[fuel.Id] += measured;
                        Event(at, 2, $"Приём топлива {fuel.Name}: {docVolume} л по документу, факт {measured} л");
                    }));
                }
            }

            foreach (var (_, act) in events.OrderBy(e => e.At)) act();

            // weekly goods restock (some items deliberately not restocked → stockouts)
            if (day % 7 == 3)
            {
                _billH++;
                Add("trInBillH", $"{_billH}", null, new() { ["InBillHID"] = _billH, ["NDoc"] = _billH, ["DateDoc"] = Ts(start.AddHours(3)), ["PartnerID"] = 1, ["StorageID"] = 1, ["IsReturn"] = 0 });
                foreach (var it in items.Where(i => i.Weight > 0 && i.Id % 9 != 0))
                {
                    var need = Target(it) - it.Stock;
                    if (need <= 0) continue;
                    _billD++;
                    Add("trInBillD", $"{_billD}", null, new() { ["InBillDID"] = _billD, ["InBillHID"] = _billH, ["ItemID"] = it.Id, ["Quantity"] = need, ["Price"] = it.Price, ["Total"] = it.Price * need });
                    Move(it, need, start.AddHours(3), 1, _billH);
                }
            }

            // shift close: tanks, counters, cash
            var cashIn = 3000m;
            Cash(start.AddMinutes(10), 14, _sessionId, 1, cashIn);
            if (!open) Cash(sessionEnd!.Value, 15, _sessionId, 1, -(cashFuel + cashShop + cashIn));
            foreach (var f in fuels)
            {
                Add("flSesTanks", $"{_sessionId}|{f.Id}", _sessionId, new()
                {
                    ["SessionID"] = _sessionId, ["TankID"] = f.Id, ["TankNum"] = f.Id, ["TankName"] = $"Р{f.Id} {f.Name}", ["FuelName"] = f.Name, ["Price"] = f.Price,
                    ["StartFuelVolume"] = Math.Round(startStock[f.Id], 2), ["ReceptVolume"] = received[f.Id], ["PumpVolume"] = Math.Round(pumped[f.Id], 2), ["FactFuelVolume"] = null,
                    ["LossVolume"] = 0, ["SurplusVolume"] = 0, ["StartMass"] = 0, ["Mass"] = null, ["Density"] = null, ["Temperature"] = null, ["Height"] = null, ["Water"] = null,
                });
                foreach (var h in f.Hoses)
                {
                    Add("flSesHoses", $"{_sessionId}|{h.Id}", _sessionId, new()
                    {
                        ["SessionID"] = _sessionId, ["HoseID"] = h.Id, ["ThroughNumber"] = h.Id, ["StartAdder"] = Math.Round(hoseStart[h.Id], 2), ["EndAdder"] = Math.Round(h.Counter, 2),
                        ["TankNum"] = f.Id, ["FuelName"] = f.Name, ["PumpNum"] = h.Pump, ["NumInPump"] = h.NumInPump, ["AdderSize"] = 7,
                    });
                }
            }
            // anomaly: fuel dispensed between shifts, past the cash register (counter jumps)
            if (anomalies && day == Math.Max(1, (int)(to - from).TotalDays - 12))
            {
                var h = fuels[0].Hoses[0];
                h.Counter += 37.5m;
                fuels[0].Stock -= 37.5m;
            }
            if (!open) Event(sessionEnd!.Value, 1, $"Закрытие смены №{_sessionId}");
        }
        return _rows;
    }

    private decimal Target(ItemState it) => Math.Max(4, Math.Round((decimal)(it.Weight * 1.6 * scale) + 6));

    private ItemState? PickItem(List<ItemState> items)
    {
        var total = items.Sum(i => i.Weight);
        var x = rnd.NextDouble() * total;
        foreach (var i in items) { x -= i.Weight; if (x <= 0) return i; }
        return null;
    }

    private (decimal Volume, decimal Ordered, decimal? Prepaid) Fill(FuelState f, double avg)
    {
        var r = rnd.NextDouble();
        if (r < 0.32) // prepaid by amount (500 / 1000 / 1500 / 2000 ₽)
        {
            var sum = new[] { 500m, 1000m, 1000m, 1500m, 2000m, 3000m }[rnd.Next(6)];
            var vol = Math.Round(sum / f.Price, 2);
            return (vol, vol, sum);
        }
        if (r < 0.75) // by liters
        {
            var liters = avg > 50 ? new[] { 50m, 80m, 100m, 150m }[rnd.Next(4)] : new[] { 10m, 20m, 20m, 30m, 40m }[rnd.Next(5)];
            return (liters, liters, null);
        }
        var full = Math.Round((decimal)(avg * (0.7 + rnd.NextDouble() * 0.9)), 2); // full tank
        return (full, 0, null);
    }

    private double HourOfDay()
    {
        // city: morning and evening peaks; highway: flatter with a night share
        double[] w = p.City
            ? [0.3, 0.2, 0.15, 0.15, 0.25, 0.6, 1.4, 2.4, 2.6, 2.0, 1.7, 1.7, 1.9, 1.8, 1.8, 2.0, 2.4, 2.9, 3.0, 2.4, 1.7, 1.2, 0.8, 0.5]
            : [0.9, 0.8, 0.7, 0.7, 0.8, 1.0, 1.4, 1.8, 2.0, 2.0, 2.0, 2.1, 2.2, 2.2, 2.1, 2.1, 2.2, 2.3, 2.2, 1.9, 1.6, 1.4, 1.2, 1.0];
        // hours from 08:00 (shift start)
        var total = w.Sum();
        var x = rnd.NextDouble() * total;
        for (var i = 0; i < 24; i++) { x -= w[(i + 8) % 24]; if (x <= 0) return i; }
        return 23;
    }

    private int Poisson(double lambda)
    {
        if (lambda > 50) return Math.Max(0, (int)Math.Round(lambda + Math.Sqrt(lambda) * Normal()));
        var l = Math.Exp(-lambda); var k = 0; var p2 = 1.0;
        do { k++; p2 *= rnd.NextDouble(); } while (p2 > l);
        return k - 1;
    }

    private double Normal() => Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble());
    private double LogNormal(double sigma) => Math.Exp(sigma * Normal() - sigma * sigma / 2);

    private string Ts(DateTime local)
    {
        var offset = _tz.GetUtcOffset(local);
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffzzz", CultureInfo.InvariantCulture);
    }

    private void Move(ItemState it, decimal qty, DateTime at, int docType, int docId)
    {
        it.Stock += qty;
        _rgItem++;
        Add("rgItemRests", $"{_rgItem}", null, new()
        {
            ["rgItemRestID"] = _rgItem, ["Date"] = Ts(at), ["DocTypeID"] = docType, ["DocID"] = docId, ["DocPositionID"] = 0, ["StorageID"] = 1, ["ItemID"] = it.Id, ["Quantity"] = qty,
        });
    }

    private void Cash(DateTime at, int docType, int docId, int cashId, decimal amount)
    {
        _rgCash++;
        Add("rgCashes", $"{_rgCash}", _sessionId, new()
        {
            ["RgCashID"] = _rgCash, ["Date"] = Ts(at), ["DocTypeID"] = docType, ["DocID"] = docId, ["DocPositionID"] = 0, ["CashID"] = cashId, ["Amount"] = amount, ["SessionID"] = _sessionId,
        });
    }

    private void Event(DateTime at, int category, string text)
    {
        _eventId++;
        Add("sysEvents", $"{_eventId}", null, new() { ["EventID"] = _eventId, ["DateTime"] = Ts(at), ["EventCategoryID"] = category, ["EventString"] = text, ["ComputerName"] = "AZS-PC", ["EventSource"] = "Демо" });
    }

    private void Ref(string dataset, string key, JsonObject data) => Add(dataset, key, null, data);

    private void Add(string dataset, string key, long? session, JsonObject data)
    {
        var json = data.ToJsonString();
        _rows.Add(new RecordChange
        {
            Dataset = dataset, Op = Protocol.OpUpsert, Key = key, SessionId = session, Data = data,
            Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataset + key + json))).ToLowerInvariant(),
        });
    }

    private sealed class FuelState(int id, string name, decimal price, double litersPerDay, decimal capacity)
    {
        public int Id { get; } = id;
        public string Name { get; } = name;
        public decimal BasePrice { get; } = price;
        public decimal Price { get; set; } = price;
        public double LitersPerDay { get; } = litersPerDay;
        public decimal Capacity { get; } = capacity;
        public decimal Stock { get; set; }
        public List<HoseState> Hoses { get; } = new();
    }

    private sealed class HoseState(int id, int pump, int numInPump, decimal counter)
    {
        public int Id { get; } = id;
        public int Pump { get; } = pump;
        public int NumInPump { get; } = numInPump;
        public decimal Counter { get; set; } = counter;
    }

    private sealed class ItemState(int id, string name, string code, int group, decimal price, double weight)
    {
        public int Id { get; } = id;
        public string Name { get; } = name;
        public string Code { get; } = code;
        public int Group { get; } = group;
        public decimal Price { get; } = price;
        public double Weight { get; } = weight;
        public decimal Stock { get; set; }
    }
}

internal static class Sender
{
    public static async Task SendAsync(HttpClient http, string adminToken, string name, string timeZone, List<RecordChange> rows)
    {
        // Connect like a real agent: request → approval (admin API, with the code) → claim the token.
        var secret = RegistrationCode.NewSecret();
        var created = await http.PostAsJsonAsync("api/v1/registrations", new RegistrationRequest
            { StationName = name, ClaimHash = RegistrationCode.ClaimHash(secret), AgentVersion = "demo", MachineName = "DEMO" }, Protocol.Json);
        if (!created.IsSuccessStatusCode)
            throw new InvalidOperationException($"connection request {name}: {(int)created.StatusCode} {await created.Content.ReadAsStringAsync()}");
        var reg = (await created.Content.ReadFromJsonAsync<RegistrationResponse>(Protocol.Json))!;
        using (var approve = new HttpRequestMessage(HttpMethod.Post, $"api/v1/admin/registrations/{reg.RegistrationId}/approve")
               { Content = JsonContent.Create(new { code = reg.VerificationCode }) })
        {
            approve.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            var res = await http.SendAsync(approve);
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"approve {name}: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()} (station name taken? use another --prefix)");
        }
        var claim = await http.PostAsJsonAsync("api/v1/registrations/claim", new RegistrationClaimRequest { RegistrationId = reg.RegistrationId, Secret = secret }, Protocol.Json);
        var token = (await claim.Content.ReadFromJsonAsync<RegistrationClaimResponse>(Protocol.Json))!.Token
                    ?? throw new InvalidOperationException($"claim {name}: {(int)claim.StatusCode}");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await http.PostAsJsonAsync("api/v1/heartbeat", new HeartbeatRequest { AgentVersion = "demo", TimeZone = timeZone, TopazDbOk = true, StartedAt = DateTimeOffset.UtcNow }, Protocol.Json)).EnsureSuccessStatusCode();

        var seq = 0L;
        foreach (var chunk in rows.Chunk(2000))
        {
            var batch = new BatchEnvelope { BatchId = Guid.NewGuid(), Seq = ++seq, CreatedAt = DateTimeOffset.UtcNow, AgentVersion = "demo", Items = chunk.ToList() };
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true)) JsonSerializer.Serialize(gz, batch, Protocol.Json);
            for (var attempt = 1; ; attempt++)
            {
                using var content = new ByteArrayContent(ms.ToArray());
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                content.Headers.ContentEncoding.Add("gzip");
                using var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/batches") { Content = content };
                req.Headers.Add(Protocol.IdempotencyKeyHeader, batch.BatchId.ToString());
                var res = await http.SendAsync(req);
                if (res.IsSuccessStatusCode) break;
                if (attempt >= 5 || (int)res.StatusCode is 400 or 409 or 413 or 422)
                    throw new InvalidOperationException($"batch {seq}: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
            }
            if (seq % 20 == 0) Console.WriteLine($"  sent {seq * 2000:N0} / {rows.Count:N0}");
        }
        http.DefaultRequestHeaders.Authorization = null;
    }
}
