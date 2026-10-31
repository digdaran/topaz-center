using Dapper;
using Npgsql;

namespace Topaz.Central.Data;

/// <summary>
/// Applies embedded Data/Migrations/NNN_*.sql in order, each in its own transaction, once.
/// A Postgres advisory lock makes concurrent starts (several replicas) safe.
/// </summary>
public sealed class Migrator(NpgsqlDataSource db, ILogger<Migrator> log)
{
    private const long LockId = 0x546F70617A; // "Topaz"

    public async Task MigrateAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("SELECT pg_advisory_lock(@LockId)", new { LockId });
        try
        {
            await conn.ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    name       text PRIMARY KEY,
                    applied_at timestamptz NOT NULL DEFAULT now()
                )
                """);
            var applied = (await conn.QueryAsync<string>("SELECT name FROM schema_migrations")).ToHashSet();
            foreach (var (name, sql) in LoadScripts())
            {
                if (applied.Contains(name)) continue;
                await using var tx = await conn.BeginTransactionAsync(ct);
                await conn.ExecuteAsync(new CommandDefinition(sql, transaction: tx, cancellationToken: ct, commandTimeout: 600));
                await conn.ExecuteAsync("INSERT INTO schema_migrations (name) VALUES (@name)", new { name }, tx);
                await tx.CommitAsync(ct);
                log.LogInformation("Applied migration {Migration}", name);
            }
        }
        finally
        {
            await conn.ExecuteAsync("SELECT pg_advisory_unlock(@LockId)", new { LockId });
        }
    }

    internal static IEnumerable<(string Name, string Sql)> LoadScripts()
    {
        var asm = typeof(Migrator).Assembly;
        const string prefix = "Topaz.Central.Data.Migrations.";
        foreach (var resource in asm.GetManifestResourceNames().Where(n => n.StartsWith(prefix) && n.EndsWith(".sql")).Order(StringComparer.Ordinal))
        {
            using var reader = new StreamReader(asm.GetManifestResourceStream(resource)!);
            yield return (resource[prefix.Length..], reader.ReadToEnd());
        }
    }
}
