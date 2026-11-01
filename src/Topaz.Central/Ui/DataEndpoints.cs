using System.Globalization;
using System.Text;
using Dapper;
using Npgsql;

namespace Topaz.Central.Ui;

/// <summary>
/// Paged operation tables with CSV export. Each kind is a fixed SQL source exposing station_id,
/// utc_ts, local_date and whitelisted columns; sort and search only touch whitelisted columns.
/// </summary>
public static class DataEndpoints
{
    private sealed record Kind(string Source, string[] Columns, string DefaultSort, string[] SearchColumns);

    private static readonly Dictionary<string, Kind> Kinds = new()
    {
        ["fuel-sales"] = new("""
            SELECT station_id, sold_at AS utc_ts, local_date, station, to_char(local_ts, 'YYYY-MM-DD HH24:MI:SS') AS local_time, session_id,
                   order_num, fuel_name, tank_num, hose_num, payment_mode,
                   CASE pay_category WHEN 'cash' THEN 'Наличные' WHEN 'card' THEN 'Банковская карта' WHEN 'contract' THEN 'По договору'
                        WHEN 'fuel_card' THEN 'Топливная карта' ELSE 'Без оплаты' END AS pay_category, remark, payment_kind, volume, price, amount,
                   amount_cash, amount_cashless, amount_discount, card_code
            FROM v_fuel_sales_local
            """,
            ["station", "local_time", "session_id", "order_num", "fuel_name", "tank_num", "hose_num", "payment_mode", "pay_category", "remark", "payment_kind",
             "volume", "price", "amount", "amount_cash", "amount_cashless", "amount_discount", "card_code"],
            "local_time", ["fuel_name", "payment_mode", "card_code"]),

        ["shop-lines"] = new("""
            SELECT station_id, doc_at AS utc_ts, local_date, station, to_char(local_ts, 'YYYY-MM-DD HH24:MI:SS') AS local_time, session_id,
                   doc_num, item_name, item_code, group_name, payment_mode, is_return, quantity, price, amount
            FROM v_shop_lines_local
            """,
            ["station", "local_time", "session_id", "doc_num", "item_name", "item_code", "group_name", "payment_mode", "is_return", "quantity", "price", "amount"],
            "local_time", ["item_name", "item_code", "group_name", "payment_mode"]),

        ["fuel-receipts"] = new("""
            SELECT r.station_id, r.received_at AS utc_ts, (r.received_at AT TIME ZONE sn.time_zone)::date AS local_date, sn.station,
                   to_char(r.received_at AT TIME ZONE sn.time_zone, 'YYYY-MM-DD HH24:MI:SS') AS local_time, r.session_id, r.receipt_id,
                   vt.num AS tank_num, f.name AS fuel_name, r.doc_number, r.car_number, r.doc_volume, r.measured_volume, r.discrepancy,
                   r.doc_mass, r.doc_density, r.doc_price
            FROM fuel_receipts r
            JOIN v_station_names sn ON sn.station_id = r.station_id
            LEFT JOIN v_tanks vt ON vt.station_id = r.station_id AND vt.tank_id = r.tank_id
            LEFT JOIN v_fuels f ON f.station_id = r.station_id AND f.fuel_id = r.fuel_id
            """,
            ["station", "local_time", "session_id", "receipt_id", "tank_num", "fuel_name", "doc_number", "car_number", "doc_volume",
             "measured_volume", "discrepancy", "doc_mass", "doc_density", "doc_price"],
            "local_time", ["fuel_name", "doc_number", "car_number"]),

        ["events"] = new("""
            SELECT e.station_id, e.occurred_at AS utc_ts, (e.occurred_at AT TIME ZONE sn.time_zone)::date AS local_date, sn.station,
                   to_char(e.occurred_at AT TIME ZONE sn.time_zone, 'YYYY-MM-DD HH24:MI:SS') AS local_time, e.event_id,
                   c.data->>'EventCategoryName' AS category, e.message, e.source, e.object, e.computer_name
            FROM station_events e
            JOIN v_station_names sn ON sn.station_id = e.station_id
            LEFT JOIN records c ON c.station_id = e.station_id AND c.dataset = 'sysEventCategores' AND c.key = e.category_id::text
            """,
            ["station", "local_time", "event_id", "category", "message", "source", "object", "computer_name"],
            "local_time", ["category", "message", "source"]),

        ["cash"] = new("""
            SELECT r.station_id, (r.data->>'Date')::timestamptz AS utc_ts, ((r.data->>'Date')::timestamptz AT TIME ZONE sn.time_zone)::date AS local_date,
                   sn.station, to_char((r.data->>'Date')::timestamptz AT TIME ZONE sn.time_zone, 'YYYY-MM-DD HH24:MI:SS') AS local_time,
                   (r.data->>'SessionID')::bigint AS session_id, dt.name AS doc_type, cs.name AS cash_name, (r.data->>'Amount')::numeric AS amount
            FROM records r
            JOIN v_station_names sn ON sn.station_id = r.station_id
            LEFT JOIN v_doc_types dt ON dt.station_id = r.station_id AND dt.doc_type_id = (r.data->>'DocTypeID')::int
            LEFT JOIN v_cashes cs ON cs.station_id = r.station_id AND cs.cash_id = (r.data->>'CashID')::bigint
            WHERE r.dataset = 'rgCashes' AND NOT r.deleted
            """,
            ["station", "local_time", "session_id", "doc_type", "cash_name", "amount"],
            "local_time", ["doc_type", "cash_name"]),
    };

    public static void MapUiData(this RouteGroupBuilder ui)
    {
        ui.MapGet("/data/{kind}", Query);
    }

    private static async Task Query(string kind, HttpContext ctx, StationScope scope, UiDb db)
    {
        if (!Kinds.TryGetValue(kind, out var k)) { ctx.Response.StatusCode = 404; return; }
        var (f, error) = await UiFilter.ParseAsync(ctx, scope);
        if (error is not null) { await error.ExecuteAsync(ctx); return; }

        var q = ctx.Request.Query;
        var sort = k.Columns.Contains(q["sort"].ToString()) ? q["sort"].ToString() : k.DefaultSort;
        var dir = q["dir"] == "asc" ? "ASC" : "DESC";
        var csv = q["format"] == "csv";
        var pageSize = csv ? 200_000 : Math.Clamp(int.TryParse(q["page_size"], out var ps) ? ps : 50, 1, 500);
        var page = Math.Max(1, int.TryParse(q["page"], out var p) ? p : 1);
        var search = q["q"].ToString();
        var searchSql = string.IsNullOrWhiteSpace(search) ? "" :
            " AND (" + string.Join(" OR ", k.SearchColumns.Select(c => $"{c}::text ILIKE @search")) + ")";

        var where = $"WHERE station_id = ANY(@st) AND utc_ts >= @utcFrom AND utc_ts < @utcTo AND local_date BETWEEN @from::date AND @to::date{searchSql}";
        var args = new DynamicParameters(f!.Args);
        args.Add("search", "%" + search + "%");
        args.Add("limit", pageSize);
        args.Add("offset", (page - 1) * pageSize);

        await using var c = await db.OpenConnectionAsync(ctx.RequestAborted);
        var sql = $"SELECT {string.Join(", ", k.Columns)} FROM ({k.Source}) x {where} ORDER BY {sort} {dir} NULLS LAST LIMIT @limit OFFSET @offset";

        if (!csv)
        {
            var total = await c.ExecuteScalarAsync<long>($"SELECT count(*) FROM ({k.Source}) x {where}", args);
            var rows = await c.QueryAsync(sql, args);
            await Results.Ok(new { total, page, page_size = pageSize, columns = k.Columns, rows }).ExecuteAsync(ctx);
            return;
        }

        ctx.Response.ContentType = "text/csv; charset=utf-8";
        ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{kind}-{f.From:yyyyMMdd}-{f.To:yyyyMMdd}.csv\"";
        await using var writer = new StreamWriter(ctx.Response.Body, new UTF8Encoding(true)); // BOM: Excel detects UTF-8
        await writer.WriteAsync(string.Join(';', k.Columns) + "\r\n");
        await using var reader = await c.ExecuteReaderAsync(sql, args);
        while (await reader.ReadAsync(ctx.RequestAborted))
        {
            var values = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                values[i] = reader.IsDBNull(i) ? "" : Csv(reader.GetValue(i) switch
                {
                    decimal d => d.ToString(CultureInfo.InvariantCulture).Replace('.', ','), // decimal comma for Russian Excel (invariant globalization)
                    bool b => b ? "1" : "0",
                    IFormattable fm => fm.ToString(null, CultureInfo.InvariantCulture),
                    var o => o.ToString() ?? "",
                });
            await writer.WriteAsync(string.Join(';', values) + "\r\n");
        }
        await writer.FlushAsync(ctx.RequestAborted);
    }

    private static string Csv(string s) =>
        s.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
