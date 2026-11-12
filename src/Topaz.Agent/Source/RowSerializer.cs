using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Topaz.Agent.Source;

/// <summary>
/// Converts a Topaz row into canonical JSON and its SHA-256 hash.
/// Canonical = column order of the result set, no whitespace, fixed formats. The same row always
/// yields the same hash, which is how changes are detected without modification timestamps.
/// </summary>
public static class RowSerializer
{
    public static readonly JsonSerializerOptions CanonicalJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep Cyrillic readable and compact
        WriteIndented = false,
    };

    /// <summary>Topaz stores local wall-clock time without offset; attach the station zone offset.</summary>
    /// <summary>
    /// A column value as JSON. Binary values (BLOBs) longer than <paramref name="maxBlobBytes"/> become null:
    /// attachments such as exchange packets are not needed centrally and would blow up memory and traffic.
    /// </summary>
    public static JsonNode? ToJsonValue(object? value, string dataTypeName, TimeZoneInfo tz, int maxBlobBytes = int.MaxValue) => value switch
    {
        null or DBNull => null,
        DateTime dt when IsDate(dataTypeName) => JsonValue.Create(dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        DateTime dt => JsonValue.Create(FormatTimestamp(dt, tz)),
        DateTimeOffset dto => JsonValue.Create(dto.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffzzz", CultureInfo.InvariantCulture)),
        TimeSpan ts => JsonValue.Create(ts.ToString(@"hh\:mm\:ss\.ffff", CultureInfo.InvariantCulture)),
        string s when IsFixedChar(dataTypeName) => JsonValue.Create(Clean(s).TrimEnd(' ')),
        string s => JsonValue.Create(Clean(s)),
        decimal d => JsonValue.Create(d),
        double d => JsonValue.Create(d),
        float f => JsonValue.Create((double)f),
        long l => JsonValue.Create(l),
        int i => JsonValue.Create(i),
        short sh => JsonValue.Create(sh),
        bool b => JsonValue.Create(b),
        Guid g => JsonValue.Create(g.ToString()),
        byte[] bytes when bytes.Length > maxBlobBytes => null,
        byte[] bytes => JsonValue.Create(Convert.ToBase64String(bytes)),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    public static string FormatTimestamp(DateTime local, TimeZoneInfo tz)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // Ambiguous/invalid local times (DST transitions) resolve to the standard offset.
        var offset = tz.IsInvalidTime(unspecified) ? tz.BaseUtcOffset : tz.GetUtcOffset(unspecified);
        return new DateTimeOffset(unspecified, offset).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffzzz", CultureInfo.InvariantCulture);
    }

    public static string Hash(string canonicalJson)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson), hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string KeyOf(IReadOnlyList<object?> values) =>
        string.Join('|', values.Select(v => v switch
        {
            null or DBNull => "",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString()!.TrimEnd(' '),
        }));

    /// <summary>Builds the row object from the given reader columns, in order.</summary>
    public static JsonObject ReadRow(DbDataReader reader, IReadOnlyList<int> ordinals, TimeZoneInfo tz,
                                     int maxBlobBytes = int.MaxValue, Action<string, long>? oversized = null)
    {
        var obj = new JsonObject();
        foreach (var i in ordinals)
        {
            var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
            if (value is byte[] b && b.Length > maxBlobBytes) oversized?.Invoke(reader.GetName(i), b.Length);
            obj[reader.GetName(i)] = ToJsonValue(value, reader.GetDataTypeName(i), tz, maxBlobBytes);
        }
        return obj;
    }

    /// <summary>NUL is not allowed in PostgreSQL text/jsonb; it never carries meaning in Topaz strings.</summary>
    private static string Clean(string s) => s.Contains('\0') ? s.Replace("\0", "") : s;

    private static bool IsDate(string typeName) => typeName.Equals("DATE", StringComparison.OrdinalIgnoreCase);
    private static bool IsFixedChar(string typeName) => typeName.Equals("CHAR", StringComparison.OrdinalIgnoreCase);
}
