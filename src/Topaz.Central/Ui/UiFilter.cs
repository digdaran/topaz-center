using System.Globalization;

namespace Topaz.Central.Ui;

/// <summary>
/// The common filter of every analytics page (query string):
///   stations=uuid,uuid   (default: all stations the user may see)
///   from=yyyy-MM-dd&amp;to=yyyy-MM-dd  station-local dates, inclusive (default: current month)
///   g=hour|day|week|month          granularity of series
///   compare=prev|yoy|none          comparison period for KPIs
///   fuel=code                      reference fuel type (fuel_types.code), optional
/// Dates are local to each station; UtcFrom/UtcTo only bound the scan (index use) with a ±14 h margin.
/// </summary>
public sealed record UiFilter(Guid[] Stations, DateTime From, DateTime To, string Granularity, string Compare, string? Fuel)
{
    public static readonly string[] Granularities = ["hour", "day", "week", "month"];

    public DateTime UtcFrom => DateTime.SpecifyKind(From.AddHours(-14), DateTimeKind.Utc);
    public DateTime UtcTo => DateTime.SpecifyKind(To.AddDays(1).AddHours(14), DateTimeKind.Utc);
    public int Days => (int)(To - From).TotalDays + 1;

    /// <summary>Parameters for Dapper: @st, @from, @to, @utcFrom, @utcTo, @fuel.</summary>
    public object Args => new { st = Stations, from = From, to = To, utcFrom = UtcFrom, utcTo = UtcTo, fuel = Fuel };

    public UiFilter? ComparePeriod() => Compare switch
    {
        "prev" => this with { From = From.AddDays(-Days), To = From.AddDays(-1) },
        "yoy" => this with { From = From.AddYears(-1), To = To.AddYears(-1) },
        _ => null,
    };

    /// <summary>SQL fragment for a station-local timestamp column truncated to the granularity.</summary>
    public string Bucket(string localTs) => Granularity switch
    {
        "hour" => $"date_trunc('hour', {localTs})",
        "week" => $"date_trunc('week', {localTs})",
        "month" => $"date_trunc('month', {localTs})",
        _ => $"date_trunc('day', {localTs})",
    };

    public static async Task<(UiFilter? Filter, IResult? Error)> ParseAsync(HttpContext ctx, StationScope scope)
    {
        var q = ctx.Request.Query;
        var allowed = await scope.AllowedAsync(ctx.User);

        Guid[] stations;
        var raw = q["stations"].ToString();
        if (string.IsNullOrWhiteSpace(raw)) stations = allowed;
        else
        {
            var parsed = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).ToArray();
            if (parsed.Any(g => g == Guid.Empty)) return (null, Results.Problem(statusCode: 400, title: "stations: invalid id"));
            if (parsed.Any(g => !allowed.Contains(g))) return (null, Results.Problem(statusCode: 403, title: "No access to one of the stations"));
            stations = parsed;
        }

        var today = DateTime.UtcNow.AddHours(3).Date; // default period in Moscow time
        if (!TryDate(q["from"], new DateTime(today.Year, today.Month, 1), out var from) || !TryDate(q["to"], today, out var to))
            return (null, Results.Problem(statusCode: 400, title: "from/to must be yyyy-MM-dd"));
        if (to < from) return (null, Results.Problem(statusCode: 400, title: "to must be >= from"));
        if ((to - from).TotalDays > 3660) return (null, Results.Problem(statusCode: 400, title: "Period is limited to 10 years"));

        var g = q["g"].ToString();
        if (string.IsNullOrEmpty(g)) g = (to - from).TotalDays > 120 ? "month" : (to - from).TotalDays > 31 ? "week" : (to - from).TotalDays < 2 ? "hour" : "day";
        if (!Granularities.Contains(g)) return (null, Results.Problem(statusCode: 400, title: "g must be hour|day|week|month"));

        var compare = q["compare"].ToString();
        if (string.IsNullOrEmpty(compare)) compare = "prev";
        if (compare is not ("prev" or "yoy" or "none")) return (null, Results.Problem(statusCode: 400, title: "compare must be prev|yoy|none"));

        var fuel = q["fuel"].ToString();
        return (new UiFilter(stations, from, to, g, compare, string.IsNullOrWhiteSpace(fuel) ? null : fuel), null);
    }

    private static bool TryDate(string? s, DateTime fallback, out DateTime value)
    {
        if (string.IsNullOrEmpty(s)) { value = fallback; return true; }
        return DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }
}
