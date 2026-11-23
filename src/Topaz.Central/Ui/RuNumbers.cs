using System.Globalization;

namespace Topaz.Central.Ui;

/// <summary>
/// Numbers for CSV files opened in a Russian Excel: decimal comma, no grouping. The service runs with invariant
/// globalization (no ICU in the image), so CultureInfo.GetCultureInfo("ru-RU") throws there — use this instead.
/// </summary>
public static class RuNumbers
{
    public static readonly NumberFormatInfo Format = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = "" };
}
