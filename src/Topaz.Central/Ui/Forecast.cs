namespace Topaz.Central.Ui;

/// <summary>
/// Simple, explainable demand forecast: for each future day, the mean of the last 4 same weekdays,
/// times a damped trend (√ of last-14-days / previous-14-days, limited to ±20%). Shown as "оценка".
/// </summary>
public static class Forecast
{
    public sealed record Point(DateTime Date, decimal Value);

    /// <summary>
    /// Date the stock runs out at the current sales speed, or null when there is no stock or it lasts over 10 years
    /// (Topaz keeps a placeholder stock like 999 999 for tea/coffee; such a date would also overflow DateTime).
    /// </summary>
    public static DateTime? StockoutDate(DateTime today, decimal stock, decimal? coverDays) =>
        stock > 0 && coverDays is { } d and >= 0 and <= 3650 ? today.Date.AddDays((double)Math.Floor(d)) : null;

    public static List<Point> Daily(IReadOnlyDictionary<DateTime, decimal> history, DateTime lastActual, int days)
    {
        decimal V(DateTime d) => history.TryGetValue(d, out var v) ? v : 0;
        var last14 = Enumerable.Range(0, 14).Sum(i => V(lastActual.AddDays(-i)));
        var prev14 = Enumerable.Range(14, 14).Sum(i => V(lastActual.AddDays(-i)));
        var trend = prev14 > 0 ? (decimal)Math.Sqrt((double)(last14 / prev14)) : 1m;
        trend = Math.Clamp(trend, 0.8m, 1.2m);

        var result = new List<Point>();
        for (var i = 1; i <= days; i++)
        {
            var day = lastActual.AddDays(i);
            var same = Enumerable.Range(1, 4).Select(w => day.AddDays(-7 * w)).Where(d => d <= lastActual).Select(V).ToList();
            var baseValue = same.Count > 0 ? same.Average() : 0;
            result.Add(new Point(day, Math.Round(baseValue * trend, 2)));
        }
        return result;
    }

    /// <summary>XYZ by the coefficient of variation of weekly sales: X &lt; 10%, Y &lt; 25%, Z otherwise.</summary>
    public static (string Class, decimal? Cv) Xyz(IReadOnlyList<decimal> weekly)
    {
        if (weekly.Count < 4) return ("—", null);
        var mean = weekly.Average();
        if (mean == 0) return ("Z", null);
        var sd = (decimal)Math.Sqrt(weekly.Sum(v => Math.Pow((double)(v - mean), 2)) / weekly.Count);
        var cv = sd / mean;
        return (cv < 0.10m ? "X" : cv < 0.25m ? "Y" : "Z", Math.Round(cv, 4));
    }

    /// <summary>ABC by cumulative share of the value: A up to 80%, B up to 95%, C the rest.</summary>
    public static Dictionary<string, string> Abc(IEnumerable<(string Key, decimal Value)> items)
    {
        var list = items.OrderByDescending(i => i.Value).ToList();
        var total = list.Sum(i => Math.Max(0, i.Value));
        var result = new Dictionary<string, string>();
        decimal cumulative = 0;
        foreach (var (key, value) in list)
        {
            // An item is A while the share *before* it is under 80% (the item crossing the line is still A).
            var before = total > 0 ? cumulative / total : 1;
            result[key] = value <= 0 ? "C" : before < 0.80m ? "A" : before < 0.95m ? "B" : "C";
            cumulative += Math.Max(0, value);
        }
        return result;
    }
}
