using System.Text.RegularExpressions;

namespace Topaz.Agent.Configuration;

/// <summary>
/// Topaz uses case-sensitive quoted identifiers ("flOrders"), so every identifier is validated
/// against a strict pattern and then double-quoted. Nothing from config is ever concatenated raw.
/// </summary>
public static partial class SqlIdentifier
{
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_$]{0,62}$")]
    private static partial Regex Pattern();

    public static bool IsValid(string? identifier) => identifier is not null && Pattern().IsMatch(identifier);

    public static string Quote(string identifier) =>
        IsValid(identifier) ? $"\"{identifier}\"" : throw new ArgumentException($"Invalid SQL identifier '{identifier}'.");
}
