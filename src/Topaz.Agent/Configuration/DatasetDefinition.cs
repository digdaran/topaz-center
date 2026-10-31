using System.Text.Json;
using System.Text.Json.Serialization;

namespace Topaz.Agent.Configuration;

public enum DatasetKind
{
    /// <summary>Small/reference table: read fully, diff by row hash, deletions detected.</summary>
    Snapshot,
    /// <summary>Shift data: rows of sessions in the current window, diff by row hash, deletions detected within the window.</summary>
    Session,
    /// <summary>Append-only log: rows with id &gt; watermark (with overlap re-read), no deletions.</summary>
    Incremental,
}

/// <summary>One Topaz table to export. Loaded from datasets.json, so tables can be added without a rebuild.</summary>
public sealed class DatasetDefinition
{
    /// <summary>Dataset name on the wire. Defaults to the table name.</summary>
    public string? Name { get; set; }
    public string Table { get; set; } = "";
    public DatasetKind Kind { get; set; }
    /// <summary>Primary key columns. Incremental datasets need exactly one integer column.</summary>
    public string[] Key { get; set; } = [];
    /// <summary>Session datasets: column holding the Topaz SessionID (direct tables).</summary>
    public string SessionColumn { get; set; } = "SessionID";
    /// <summary>Session datasets without their own SessionID: join to the parent that has one.</summary>
    public ParentLink? Parent { get; set; }
    /// <summary>Incremental datasets: how many ids below the watermark are re-read (late commits).</summary>
    public int Overlap { get; set; } = 1000;
    /// <summary>Columns never sent (passwords, device configs).</summary>
    public string[] Exclude { get; set; } = [];
    /// <summary>Snapshot datasets: override of Collection:SnapshotIntervalSeconds.</summary>
    public int? IntervalSeconds { get; set; }
    public bool Enabled { get; set; } = true;

    [JsonIgnore] public string DatasetName => string.IsNullOrWhiteSpace(Name) ? Table : Name!;
}

public sealed class ParentLink
{
    public string Table { get; set; } = "";
    /// <summary>Parent primary key column.</summary>
    public string Key { get; set; } = "";
    /// <summary>Child column referencing the parent key.</summary>
    public string ForeignKey { get; set; } = "";
    public string SessionColumn { get; set; } = "SessionID";
}

public sealed class DatasetCatalog
{
    public List<DatasetDefinition> Datasets { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static DatasetCatalog Load(string path)
    {
        var catalog = JsonSerializer.Deserialize<DatasetCatalog>(File.ReadAllText(path), Options)
                      ?? throw new InvalidOperationException($"Empty dataset catalog: {path}");
        var errors = catalog.Validate().ToList();
        if (errors.Count > 0)
            throw new InvalidOperationException($"Invalid dataset catalog {path}:\n - " + string.Join("\n - ", errors));
        return catalog;
    }

    public IEnumerable<string> Validate()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in Datasets)
        {
            var n = d.DatasetName;
            if (!names.Add(n)) yield return $"{n}: duplicate dataset name";
            if (!SqlIdentifier.IsValid(d.Table)) yield return $"{n}: invalid table name '{d.Table}'";
            if (!System.Text.RegularExpressions.Regex.IsMatch(n, "^[A-Za-z][A-Za-z0-9_]{0,63}$"))
                yield return $"{n}: dataset name must match ^[A-Za-z][A-Za-z0-9_]{{0,63}}$";
            if (d.Key.Length == 0) yield return $"{n}: key is required";
            foreach (var c in d.Key.Concat(d.Exclude).Append(d.SessionColumn))
                if (!SqlIdentifier.IsValid(c)) yield return $"{n}: invalid column name '{c}'";
            if (d.Kind == DatasetKind.Incremental && d.Key.Length != 1)
                yield return $"{n}: incremental dataset needs exactly one key column";
            if (d.Kind == DatasetKind.Incremental && d.Overlap < 0)
                yield return $"{n}: overlap must be >= 0";
            if (d.Parent is { } p)
            {
                if (d.Kind != DatasetKind.Session) yield return $"{n}: parent is only allowed for session datasets";
                foreach (var id in new[] { p.Table, p.Key, p.ForeignKey, p.SessionColumn })
                    if (!SqlIdentifier.IsValid(id)) yield return $"{n}: invalid parent identifier '{id}'";
            }
        }
    }
}
