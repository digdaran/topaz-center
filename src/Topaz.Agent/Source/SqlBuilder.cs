using System.Globalization;
using Topaz.Agent.Configuration;

namespace Topaz.Agent.Source;

/// <summary>
/// Builds the only statements the agent ever runs against Topaz: SELECTs.
/// Identifiers are validated and quoted; values go through parameters.
/// </summary>
public static class SqlBuilder
{
    /// <summary>Alias of the technical column carrying SessionID (session datasets) or the id (incremental).</summary>
    public const string ScopeAlias = "__scope";

    public static string Snapshot(DatasetDefinition d) =>
        $"SELECT t.* FROM {Q(d.Table)} t";

    /// <summary>Rows of the given sessions. Parameters @s0..@sN hold the SessionIDs.</summary>
    public static string Session(DatasetDefinition d, int sessionCount)
    {
        if (sessionCount <= 0) throw new ArgumentOutOfRangeException(nameof(sessionCount));
        var inList = string.Join(", ", Enumerable.Range(0, sessionCount).Select(i => $"@s{i}"));
        if (d.Parent is { } p)
            return $"SELECT t.*, p.{Q(p.SessionColumn)} AS {Q(ScopeAlias)} FROM {Q(d.Table)} t " +
                   $"JOIN {Q(p.Table)} p ON p.{Q(p.Key)} = t.{Q(p.ForeignKey)} " +
                   $"WHERE p.{Q(p.SessionColumn)} IN ({inList})";
        return $"SELECT t.*, t.{Q(d.SessionColumn)} AS {Q(ScopeAlias)} FROM {Q(d.Table)} t " +
               $"WHERE t.{Q(d.SessionColumn)} IN ({inList})";
    }

    /// <summary>Rows with id &gt; @from in id order, at most <paramref name="limit"/>.</summary>
    public static string Incremental(DatasetDefinition d, int limit)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        var id = Q(d.Key[0]);
        return $"SELECT FIRST {limit.ToString(CultureInfo.InvariantCulture)} t.*, t.{id} AS {Q(ScopeAlias)} " +
               $"FROM {Q(d.Table)} t WHERE t.{id} > @from ORDER BY t.{id}";
    }

    public const string SessionsOpen =
        "SELECT \"SessionID\" FROM \"sysSessions\" WHERE \"EndDateTime\" IS NULL";

    public static string SessionsLastClosed(int count) =>
        $"SELECT FIRST {count.ToString(CultureInfo.InvariantCulture)} \"SessionID\" FROM \"sysSessions\" " +
        "WHERE \"EndDateTime\" IS NOT NULL ORDER BY \"SessionID\" DESC";

    public static string SessionsAfter(int count) =>
        $"SELECT FIRST {count.ToString(CultureInfo.InvariantCulture)} \"SessionID\" FROM \"sysSessions\" " +
        "WHERE \"SessionID\" > @cursor ORDER BY \"SessionID\"";

    public const string SessionsMax = "SELECT MAX(\"SessionID\") FROM \"sysSessions\"";

    public const string Columns =
        "SELECT TRIM(rdb$relation_name), TRIM(rdb$field_name) FROM rdb$relation_fields " +
        "WHERE rdb$system_flag = 0 OR rdb$system_flag IS NULL";

    public const string ServerVersion = "SELECT rdb$get_context('SYSTEM', 'ENGINE_VERSION') FROM rdb$database";

    private static string Q(string identifier) => SqlIdentifier.Quote(identifier);
}
