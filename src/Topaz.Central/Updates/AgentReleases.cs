using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Topaz.Contracts;

namespace Topaz.Central.Updates;

/// <summary>
/// Agent packages shipped with this server: &lt;AgentUpdatesDirectory&gt;/&lt;version&gt;/&lt;rid&gt;/{manifest.json, manifest.sig, *.zip}
/// (put there by CI). Only signed packages are offered; the server never signs or alters them, agents verify.
/// </summary>
public sealed class AgentReleases(IOptions<CentralOptions> options, NpgsqlDataSource db, ILogger<AgentReleases> log)
{
    public sealed record Package(string Version, string Rid, string Directory, AgentUpdateManifest Manifest);

    private volatile IReadOnlyList<Package> _packages = [];

    public IReadOnlyList<Package> All => _packages;

    public string Root => string.IsNullOrWhiteSpace(options.Value.AgentUpdatesDirectory)
        ? Path.Combine(AppContext.BaseDirectory, "agent-updates")
        : options.Value.AgentUpdatesDirectory!;

    /// <summary>Newest package for a runtime, or null.</summary>
    public Package? Latest(string? rid) => rid is null ? null
        : _packages.Where(p => string.Equals(p.Rid, rid, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Version, Comparer<string>.Create(SemVer.Compare)).FirstOrDefault();

    public Package? Find(string version, string rid) =>
        _packages.FirstOrDefault(p => p.Version == version && string.Equals(p.Rid, rid, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads the folder and records new versions (first_seen_at starts the pilot period).</summary>
    public async Task ScanAsync(CancellationToken ct)
    {
        var found = new List<Package>();
        if (Directory.Exists(Root))
        {
            foreach (var versionDir in Directory.GetDirectories(Root))
            foreach (var ridDir in Directory.GetDirectories(versionDir))
            {
                var manifestPath = Path.Combine(ridDir, AgentUpdateManifest.FileName);
                if (!File.Exists(manifestPath)) continue;
                if (!File.Exists(Path.Combine(ridDir, AgentUpdateManifest.SignatureFileName)))
                {
                    log.LogWarning("Agent package {Dir} is not signed; not offered", ridDir);
                    continue;
                }
                try
                {
                    var m = JsonSerializer.Deserialize<AgentUpdateManifest>(await File.ReadAllBytesAsync(manifestPath, ct), Protocol.Json)!;
                    var zip = Path.Combine(ridDir, m.File);
                    if (m.Version != Path.GetFileName(versionDir) || !string.Equals(m.Rid, Path.GetFileName(ridDir), StringComparison.OrdinalIgnoreCase)
                        || m.File != Path.GetFileName(m.File) || !File.Exists(zip) || new FileInfo(zip).Length != m.Size)
                    {
                        log.LogWarning("Agent package {Dir} does not match its manifest; not offered", ridDir);
                        continue;
                    }
                    found.Add(new Package(m.Version, m.Rid, ridDir, m));
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    log.LogWarning(ex, "Agent package {Dir} is unreadable", ridDir);
                }
            }
        }
        _packages = found;

        await using var c = await db.OpenConnectionAsync(ct);
        await c.ExecuteAsync("UPDATE agent_releases SET present = false");
        foreach (var p in found)
            await c.ExecuteAsync("""
                INSERT INTO agent_releases (version, rid, file, sha256, size) VALUES (@Version, @Rid, @File, @Sha256, @Size)
                ON CONFLICT (version, rid) DO UPDATE SET present = true, file = EXCLUDED.file, sha256 = EXCLUDED.sha256, size = EXCLUDED.size
                """, new { p.Version, p.Rid, p.Manifest.File, p.Manifest.Sha256, p.Manifest.Size });
        log.LogInformation("Agent packages available: {Packages}", found.Count == 0 ? "none" : string.Join(", ", found.Select(p => $"{p.Version}/{p.Rid}")));
    }
}
