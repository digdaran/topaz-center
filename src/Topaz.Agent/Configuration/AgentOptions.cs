using FirebirdSql.Data.FirebirdClient;

namespace Topaz.Agent.Configuration;

/// <summary>
/// Agent settings. Bound from appsettings.json (next to the exe) and environment variables
/// with the TOPAZ_AGENT_ prefix, e.g. TOPAZ_AGENT_Firebird__Password, TOPAZ_AGENT_Server__EnrollmentKey.
/// </summary>
public sealed class AgentOptions
{
    public ServerOptions Server { get; set; } = new();
    public StationOptions Station { get; set; } = new();
    public FirebirdOptions Firebird { get; set; } = new();
    public CollectionOptions Collection { get; set; } = new();

    /// <summary>Local state (SQLite queue, watermarks, token, logs). Default: %ProgramData%\TopazAgent.</summary>
    public string? StateDirectory { get; set; }

    /// <summary>Dataset catalog file; relative paths are resolved against the exe directory.</summary>
    public string DatasetsFile { get; set; } = "datasets.json";

    public string ResolveStateDirectory()
    {
        if (!string.IsNullOrWhiteSpace(StateDirectory))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(StateDirectory));
        var root = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : Path.Combine(AppContext.BaseDirectory, "state");
        return Path.Combine(root, "TopazAgent");
    }

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Station.Name))
            yield return "Station:Name is required (station name given at install time).";
        else if (Station.Name.Trim().Length > 100)
            yield return "Station:Name must be at most 100 characters.";

        if (!Uri.TryCreate(Server.Url, UriKind.Absolute, out var uri))
            yield return "Server:Url must be an absolute URL, e.g. https://topaz.example.com.";
        else if (uri.Scheme != Uri.UriSchemeHttps && !Server.AllowInsecureHttp)
            yield return "Server:Url must use https (set Server:AllowInsecureHttp=true only for local testing).";

        if (string.IsNullOrWhiteSpace(Firebird.Database))
            yield return "Firebird:Database is required (path to the Topaz .fdb file or alias).";
        if (Collection.IntervalSeconds < 10)
            yield return "Collection:IntervalSeconds must be >= 10.";
        if (Collection.MaxItemsPerBatch is < 1 or > Contracts.Protocol.MaxItemsPerBatch)
            yield return $"Collection:MaxItemsPerBatch must be 1..{Contracts.Protocol.MaxItemsPerBatch}.";
        if (!string.IsNullOrWhiteSpace(Station.TimeZone) && !TryFindTimeZone(Station.TimeZone, out _))
            yield return $"Station:TimeZone '{Station.TimeZone}' is unknown.";
    }

    public TimeZoneInfo ResolveTimeZone() =>
        string.IsNullOrWhiteSpace(Station.TimeZone) ? TimeZoneInfo.Local
        : TryFindTimeZone(Station.TimeZone!, out var tz) ? tz! : TimeZoneInfo.Local;

    private static bool TryFindTimeZone(string id, out TimeZoneInfo? tz)
    {
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
        catch (Exception) { tz = null; return false; }
    }
}

public sealed class ServerOptions
{
    public string Url { get; set; } = "";
    /// <summary>Shared install key, used only once to obtain the station token. Pass via env, not the file.</summary>
    public string? EnrollmentKey { get; set; }
    public bool AllowInsecureHttp { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
    public int HeartbeatSeconds { get; set; } = 60;
    public int RetryBaseSeconds { get; set; } = 5;
    public int RetryMaxSeconds { get; set; } = 900;
}

public sealed class StationOptions
{
    /// <summary>Station name entered at install time; identifies the station on the server.</summary>
    public string Name { get; set; } = "";
    /// <summary>Time zone of Topaz local timestamps (Windows or IANA id). Default: OS local zone.</summary>
    public string? TimeZone { get; set; }
}

public sealed class FirebirdOptions
{
    public string DataSource { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3050;
    public string Database { get; set; } = "";
    public string User { get; set; } = "SYSDBA";
    /// <summary>Pass via env TOPAZ_AGENT_Firebird__Password.</summary>
    public string Password { get; set; } = "";
    public string Charset { get; set; } = "UTF8";
    public int ConnectionTimeoutSeconds { get; set; } = 15;

    public string BuildConnectionString() => new FbConnectionStringBuilder
    {
        DataSource = DataSource,
        Port = Port,
        Database = Database,
        UserID = User,
        Password = Password,
        Charset = Charset,
        Dialect = 3,
        ConnectionTimeout = ConnectionTimeoutSeconds,
        Pooling = false, // one short connection per cycle; nothing held open between cycles
        ApplicationName = "TopazAgent",
    }.ToString();
}

public sealed class CollectionOptions
{
    /// <summary>Main cycle: shift-scoped and incremental datasets.</summary>
    public int IntervalSeconds { get; set; } = 60;
    /// <summary>Default re-read interval for snapshot (reference) datasets.</summary>
    public int SnapshotIntervalSeconds { get; set; } = 900;
    /// <summary>How many latest closed shifts are re-read every cycle (late corrections).</summary>
    public int ClosedSessionsWindow { get; set; } = 3;
    /// <summary>How many older shifts are walked per cycle during initial load / periodic rescan.</summary>
    public int BackfillSessionsPerCycle { get; set; } = 20;
    /// <summary>Every N hours all shifts are re-walked (hash diff, so only real changes are sent).</summary>
    public int FullRescanHours { get; set; } = 24;
    public int MaxItemsPerBatch { get; set; } = 500;
    /// <summary>Max rows read per cycle from an incremental dataset.</summary>
    public int IncrementalRowsPerCycle { get; set; } = 5000;
    /// <summary>Collection pauses (data stays in Topaz) while the unsent queue is larger than this.</summary>
    public long MaxQueueBytes { get; set; } = 2L * 1024 * 1024 * 1024;
}
