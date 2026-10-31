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
}

public sealed class HeartbeatResponse
{
    public DateTimeOffset ServerTime { get; set; }
}
