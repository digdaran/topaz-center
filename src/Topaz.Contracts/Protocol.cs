using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Topaz.Contracts;

/// <summary>Wire protocol shared by the station agent and the central service (API v1).</summary>
public static class Protocol
{
    public const int SchemaVersion = 1;
    public const string ApiPrefix = "/api/v1";

    public const int MaxItemsPerBatch = 5000;
    public const int MaxKeyLength = 200;
    public const int MaxDatasetLength = 64;

    public const string OpUpsert = "upsert";
    public const string OpDelete = "delete";

    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string SeqHeader = "X-Batch-Seq";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Data column names come from Topaz and are kept verbatim (dictionary keys are not renamed).
        DictionaryKeyPolicy = null,
    };
}

/// <summary>A batch of record changes from one station. Sent as-is on every retry (same batch_id).</summary>
public sealed class BatchEnvelope
{
    public int SchemaVersion { get; set; } = Protocol.SchemaVersion;
    public Guid BatchId { get; set; }
    /// <summary>Monotonic per-agent-installation number; used for gap/ordering diagnostics.</summary>
    public long Seq { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string AgentVersion { get; set; } = "";
    public List<RecordChange> Items { get; set; } = new();
}

/// <summary>Full current state of one source row (upsert) or its removal (delete).</summary>
public sealed class RecordChange
{
    /// <summary>Topaz table name, e.g. "flOrders".</summary>
    public string Dataset { get; set; } = "";
    public string Op { get; set; } = Protocol.OpUpsert;
    /// <summary>Primary key value(s) of the source row; composite keys are joined with '|'.</summary>
    public string Key { get; set; } = "";
    /// <summary>SHA-256 (hex) of the canonical row JSON; change-detection token.</summary>
    public string? Hash { get; set; }
    /// <summary>Topaz SessionID (shift) the row belongs to, if any.</summary>
    public long? SessionId { get; set; }
    public JsonObject? Data { get; set; }
}

public sealed class BatchAck
{
    /// <summary>"accepted" for a new batch, "duplicate" when this batch_id was already stored.</summary>
    public string Status { get; set; } = "";
    public Guid BatchId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

public sealed class EnrollRequest
{
    public string StationName { get; set; } = "";
    public string EnrollmentKey { get; set; } = "";
    public string? MachineName { get; set; }
    public string? AgentVersion { get; set; }
}

public sealed class EnrollResponse
{
    public Guid StationId { get; set; }
    public string StationName { get; set; } = "";
    /// <summary>Station bearer token. Returned once; the server keeps only its hash.</summary>
    public string Token { get; set; } = "";
}

public sealed class HeartbeatRequest
{
    public string AgentVersion { get; set; } = "";
    public string? MachineName { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public int QueuePending { get; set; }
    public int QueueDead { get; set; }
    public long QueueBytes { get; set; }
    public DateTimeOffset? OldestPendingAt { get; set; }
    public DateTimeOffset? LastCollectAt { get; set; }
    public string? LastCollectError { get; set; }
    public DateTimeOffset? LastSendAt { get; set; }
    public string? LastSendError { get; set; }
    public bool TopazDbOk { get; set; }
    public string? TopazDbVersion { get; set; }
    public long? LastSessionId { get; set; }
    public bool? LastSessionOpen { get; set; }
    /// <summary>IANA time zone of the Topaz local timestamps (station clock).</summary>
    public string? TimeZone { get; set; }
    /// <summary>Runtime of this build ("win-x64", "win-x86"); agents without it get no update offers.</summary>
    public string? Rid { get; set; }
    /// <summary>State of the latest self-update (absent if none happened).</summary>
    public AgentUpdateReport? Update { get; set; }
}

public sealed class HeartbeatResponse
{
    public DateTimeOffset ServerTime { get; set; }
    /// <summary>Set when the server wants this agent to install another version.</summary>
    public AgentUpdateOffer? Update { get; set; }
}

public static class AgentUpdateStatus
{
    public const string Downloading = "downloading";
    public const string Installing = "installing";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

public sealed class AgentUpdateReport
{
    public string Version { get; set; } = "";
    /// <summary>One of <see cref="AgentUpdateStatus"/>.</summary>
    public string Status { get; set; } = "";
    public string? Error { get; set; }
    public DateTimeOffset At { get; set; }
}

public sealed class AgentUpdateOffer
{
    public string Version { get; set; } = "";
    public string Rid { get; set; } = "";
}

/// <summary>
/// Describes one agent package; built and signed in CI (manifest.sig = ECDSA P-256 / SHA-256 over the exact
/// manifest.json bytes). The server only distributes it: agents verify the signature with their built-in key.
/// Files: GET /api/v1/agent/updates/{version}/{rid}/{manifest.json | manifest.sig | file}.
/// </summary>
public sealed class AgentUpdateManifest
{
    public const string FileName = "manifest.json";
    public const string SignatureFileName = "manifest.sig";

    public string Version { get; set; } = "";
    public string Rid { get; set; } = "";
    /// <summary>Zip file name in the same folder.</summary>
    public string File { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

/// <summary>Semantic version ordering: 1.2.0 > 1.2.0-rc.1 > 1.1.9; build metadata (+...) is ignored.</summary>
public static class SemVer
{
    public static bool TryParse(string? s, out (int[] Core, string[] Pre) v)
    {
        v = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var text = s.Trim().TrimStart('v', 'V').Split('+')[0];
        var dash = text.IndexOf('-');
        var core = (dash < 0 ? text : text[..dash]).Split('.');
        if (core.Length is < 1 or > 4) return false;
        var nums = new int[3];
        for (var i = 0; i < core.Length && i < 3; i++)
            if (!int.TryParse(core[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out nums[i])) return false;
        v = (nums, dash < 0 ? [] : text[(dash + 1)..].Split('.'));
        return true;
    }

    /// <summary>Negative if a &lt; b; unparsable versions sort first.</summary>
    public static int Compare(string? a, string? b)
    {
        var okA = TryParse(a, out var x);
        var okB = TryParse(b, out var y);
        if (!okA || !okB) return okA.CompareTo(okB);
        for (var i = 0; i < 3; i++)
            if (x.Core[i] != y.Core[i]) return x.Core[i].CompareTo(y.Core[i]);
        if (x.Pre.Length == 0 || y.Pre.Length == 0) return y.Pre.Length.CompareTo(x.Pre.Length); // release > pre-release
        for (var i = 0; i < Math.Min(x.Pre.Length, y.Pre.Length); i++)
        {
            var nx = int.TryParse(x.Pre[i], out var ix);
            var ny = int.TryParse(y.Pre[i], out var iy);
            var c = nx && ny ? ix.CompareTo(iy) : nx != ny ? (nx ? -1 : 1) : string.CompareOrdinal(x.Pre[i], y.Pre[i]);
            if (c != 0) return c;
        }
        return x.Pre.Length.CompareTo(y.Pre.Length);
    }
}
