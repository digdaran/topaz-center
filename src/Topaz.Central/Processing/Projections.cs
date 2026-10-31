using System.Text;

namespace Topaz.Central.Processing;

/// <summary>How a JSON field from Topaz is cast into a typed column.</summary>
public enum ColType { BigInt, Int, Numeric, Text, Timestamp, Bool }

public sealed record Col(string Target, string Source, ColType Type);

/// <summary>
/// Maps one dataset (Topaz table) to one normalized table. Upserts and deletes are generated
/// SQL working on the jsonb row, so adding a projection is a declaration, not new code.
/// </summary>
public sealed class Projection
{
    public required string Dataset { get; init; }
    public required string Table { get; init; }
    public required Col[] Key { get; init; }
    public required Col[] Columns { get; init; }

    private string? _upsert, _delete;

    public string UpsertSql => _upsert ??= BuildUpsert();
    public string DeleteSql => _delete ??= BuildDelete();

    private string BuildUpsert()
    {
        var all = Key.Concat(Columns).ToArray();
        var sb = new StringBuilder();
        sb.Append($"INSERT INTO {Table} (station_id, {string.Join(", ", all.Select(c => c.Target))}, updated_at)\n");
        sb.Append($"SELECT @station, {string.Join(", ", all.Select(Expr))}, now() FROM (SELECT @data::jsonb AS d) s\n");
        sb.Append($"ON CONFLICT (station_id, {string.Join(", ", Key.Select(c => c.Target))}) DO UPDATE SET ");
        sb.Append(string.Join(", ", Columns.Select(c => $"{c.Target} = EXCLUDED.{c.Target}").Append("updated_at = now()")));
        return sb.ToString();
    }

    private string BuildDelete() =>
        $"DELETE FROM {Table} t USING (SELECT @data::jsonb AS d) s WHERE t.station_id = @station AND " +
        string.Join(" AND ", Key.Select(c => $"t.{c.Target} = {Expr(c)}"));

    internal static string Expr(Col c)
    {
        var v = $"(s.d->>'{c.Source.Replace("'", "''")}')";
        return c.Type switch
        {
            ColType.BigInt => $"{v}::bigint",
            ColType.Int => $"{v}::int",
            ColType.Numeric => $"{v}::numeric",
            ColType.Text => v,
            ColType.Timestamp => $"{v}::timestamptz",
            ColType.Bool => $"({v}::int <> 0)",
            _ => throw new ArgumentOutOfRangeException(nameof(c)),
        };
    }
}

public static class Projections
{
    private static Col B(string t, string s) => new(t, s, ColType.BigInt);
    private static Col I(string t, string s) => new(t, s, ColType.Int);
    private static Col N(string t, string s) => new(t, s, ColType.Numeric);
    private static Col T(string t, string s) => new(t, s, ColType.Text);
    private static Col Ts(string t, string s) => new(t, s, ColType.Timestamp);
    private static Col Bo(string t, string s) => new(t, s, ColType.Bool);

    public static readonly IReadOnlyDictionary<string, Projection> ByDataset = new Projection[]
    {
        new()
        {
            Dataset = "sysSessions", Table = "shifts", Key = [B("session_id", "SessionID")],
            Columns = [I("session_num", "SessionNum"), T("user_name", "UserName"), Ts("started_at", "StartDateTime"),
                       Ts("ended_at", "EndDateTime"), Bo("is_deleted", "Deleted")],
        },
        new()
        {
            Dataset = "flOrders", Table = "fuel_sales", Key = [B("order_id", "OrderID")],
            Columns = [B("session_id", "SessionID"), I("order_num", "OrderNum"), Ts("sold_at", "DateTime"), B("fuel_id", "FuelID"),
                       B("tank_id", "TankID"), B("hose_id", "HoseID"), B("payment_mode_id", "PaymentModeID"), N("volume", "Volume"),
                       N("volume_ordered", "VolumeOrdered"), N("price", "Price"), N("amount", "Amount"), N("amount_cash", "AmountCash"),
                       N("amount_cashless", "AmountCashless"), N("amount_discount", "AmountDiscount"), N("amount_bonuses", "AmountBonuses"),
                       N("mass", "Mass"), N("density", "Density"), N("temperature", "Temperature"), I("status_order", "StatusOrder"),
                       I("order_source", "OrderSource"), I("card_type", "CardType"), T("card_code", "CardCode"),
                       I("fiscal_number", "FiscalNumber"), B("driver_id", "DriverID"), B("agent_id", "AgentID"),
                       T("remark", "Remark"), N("price_pump", "PricePump"), T("tax_str", "TaxStr")],
        },
        new()
        {
            Dataset = "flSesTanks", Table = "tank_shift_states", Key = [B("session_id", "SessionID"), B("tank_id", "TankID")],
            Columns = [I("tank_num", "TankNum"), T("tank_name", "TankName"), T("fuel_name", "FuelName"), N("price", "Price"),
                       N("start_volume", "StartFuelVolume"), N("receipt_volume", "ReceptVolume"), N("pump_volume", "PumpVolume"),
                       N("fact_volume", "FactFuelVolume"), N("loss_volume", "LossVolume"), N("surplus_volume", "SurplusVolume"),
                       N("start_mass", "StartMass"), N("mass", "Mass"), N("density", "Density"), N("temperature", "Temperature"),
                       I("height", "Height"), I("water", "Water")],
        },
        new()
        {
            Dataset = "flSesHoses", Table = "hose_shift_counters", Key = [B("session_id", "SessionID"), B("hose_id", "HoseID")],
            Columns = [I("pump_num", "PumpNum"), I("num_in_pump", "NumInPump"), I("tank_num", "TankNum"), T("fuel_name", "FuelName"),
                       N("start_counter", "StartAdder"), N("end_counter", "EndAdder")],
        },
        new()
        {
            Dataset = "flRecepts", Table = "fuel_receipts", Key = [B("receipt_id", "ReceptID")],
            Columns = [B("session_id", "SessionID"), Ts("received_at", "ReceptDateTime"), B("tank_id", "TankID"), B("fuel_id", "FuelID"),
                       B("partner_id", "PartnerID"), T("doc_number", "DocNumber"), T("car_number", "DocCarNumber"),
                       N("doc_volume", "DocVolume"), N("doc_mass", "DocMass"), N("doc_density", "DocDensity"),
                       N("doc_temperature", "DocTemperature"), N("doc_price", "DocFuelPrice"), N("measured_volume", "MeasuredVolume"),
                       N("discrepancy", "Discrepancy")],
        },
        new()
        {
            Dataset = "trFastTradeH", Table = "shop_receipts", Key = [B("receipt_id", "FastTradeHID")],
            Columns = [B("session_id", "SessionID"), I("doc_num", "NDoc"), Ts("doc_at", "DateDoc"), Bo("is_return", "IsReturn"),
                       B("cash_id", "CashID"), B("storage_id", "StorageID"), B("partner_id", "PartnerID"),
                       B("payment_mode_id", "PaymentModeID"), N("amount_cash", "AmountCash"), N("amount_cashless", "AmountCashless"),
                       N("amount_discount", "AmountDiscount"), N("amount_bonuses", "AmountBonuses"), I("status", "Status"),
                       I("fiscal_number", "FiscalNumber")],
        },
        new()
        {
            Dataset = "trFastTradeD", Table = "shop_receipt_lines", Key = [B("line_id", "FastTradeDID")],
            Columns = [B("receipt_id", "FastTradeHID"), B("item_id", "ItemID"), B("item_unit_id", "ItemUnitID"), N("quantity", "Quantity"),
                       N("price", "Price"), N("amount", "Amount"), N("amount_discount", "AmountDiscount"), N("nds_amount", "NdsAmount"),
                       N("total", "Total")],
        },
        new()
        {
            Dataset = "rgItemRests", Table = "item_moves", Key = [B("move_id", "rgItemRestID")],
            Columns = [B("item_id", "ItemID"), B("storage_id", "StorageID"), I("doc_type_id", "DocTypeID"), B("doc_id", "DocID"),
                       Ts("moved_at", "Date"), N("quantity", "Quantity")],
        },
        new()
        {
            Dataset = "rgCashes", Table = "cash_moves", Key = [B("move_id", "RgCashID")],
            Columns = [B("session_id", "SessionID"), Ts("moved_at", "Date"), I("doc_type_id", "DocTypeID"), B("doc_id", "DocID"),
                       B("cash_id", "CashID"), N("amount", "Amount")],
        },
        new()
        {
            Dataset = "sysEvents", Table = "station_events", Key = [B("event_id", "EventID")],
            Columns = [Ts("occurred_at", "DateTime"), B("category_id", "EventCategoryID"), T("message", "EventString"),
                       T("computer_name", "ComputerName"), T("source", "EventSource"), T("object", "EventObject")],
        },
    }.ToDictionary(p => p.Dataset, StringComparer.Ordinal);
}
