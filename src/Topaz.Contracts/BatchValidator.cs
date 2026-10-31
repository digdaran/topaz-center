using System.Text.RegularExpressions;

namespace Topaz.Contracts;

/// <summary>Structural validation of a batch. Used by the server (authoritative) and by agent tests.</summary>
public static partial class BatchValidator
{
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}$")]
    private static partial Regex DatasetRegex();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashRegex();

    public static Dictionary<string, string[]> Validate(BatchEnvelope? batch)
    {
        var errors = new Dictionary<string, string[]>();
        void Add(string field, string message) =>
            errors[field] = errors.TryGetValue(field, out var e) ? [.. e, message] : [message];

        if (batch is null)
        {
            Add("body", "Batch body is required.");
            return errors;
        }
        if (batch.SchemaVersion != Protocol.SchemaVersion)
            Add("schema_version", $"Unsupported schema_version {batch.SchemaVersion}; expected {Protocol.SchemaVersion}.");
        if (batch.BatchId == Guid.Empty)
            Add("batch_id", "batch_id is required.");
        if (batch.Seq <= 0)
            Add("seq", "seq must be positive.");
        if (batch.CreatedAt == default)
            Add("created_at", "created_at is required.");
        if (batch.Items is null || batch.Items.Count == 0)
        {
            Add("items", "At least one item is required.");
            return errors;
        }
        if (batch.Items.Count > Protocol.MaxItemsPerBatch)
            Add("items", $"Too many items: {batch.Items.Count} > {Protocol.MaxItemsPerBatch}.");

        for (var i = 0; i < batch.Items.Count && errors.Count < 50; i++)
        {
            var it = batch.Items[i];
            var p = $"items[{i}]";
            if (it is null) { Add(p, "Item is null."); continue; }
            if (string.IsNullOrEmpty(it.Dataset) || !DatasetRegex().IsMatch(it.Dataset))
                Add($"{p}.dataset", "dataset must match ^[A-Za-z][A-Za-z0-9_]{0,63}$.");
            if (string.IsNullOrEmpty(it.Key) || it.Key.Length > Protocol.MaxKeyLength)
                Add($"{p}.key", $"key is required and must be at most {Protocol.MaxKeyLength} chars.");
            switch (it.Op)
            {
                case Protocol.OpUpsert:
                    if (it.Data is null) Add($"{p}.data", "data is required for upsert.");
                    if (it.Hash is null || !HashRegex().IsMatch(it.Hash))
                        Add($"{p}.hash", "hash must be 64 lowercase hex chars for upsert.");
                    break;
                case Protocol.OpDelete:
                    break;
                default:
                    Add($"{p}.op", "op must be 'upsert' or 'delete'.");
                    break;
            }
        }
        return errors;
    }
}
