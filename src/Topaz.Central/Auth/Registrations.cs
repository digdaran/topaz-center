using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;
using Topaz.Central.Processing;
using Topaz.Contracts;

namespace Topaz.Central.Auth;

/// <summary>
/// Station connection by request (docs/architecture.md, «Подключение АЗС»):
/// a new agent registers without credentials and polls; a superadmin approves it after comparing the
/// verification code shown on the station; the agent then receives its own token. No shared keys.
/// </summary>
public sealed class Registrations(NpgsqlDataSource db, StationAuthenticator auth, AnomalyDetector detector, ILogger<Registrations> log)
{
    public const int PollSeconds = 60;
    public const int MaxPendingPerIp = 5;
    public const int MaxPendingTotal = 200;
    public static readonly TimeSpan ExpireAfter = TimeSpan.FromDays(14);
    public static readonly TimeSpan RetryAfterReject = TimeSpan.FromHours(24);
    public static readonly TimeSpan BlockFor = TimeSpan.FromDays(30);

    public sealed record Outcome(int Status, object Body);

    private sealed record Row(Guid Id, string ClaimHash, string VerificationCode, string Status, Guid? StationId, string StationName, string? Ip);

    // ---------------------------------------------------------------- agent side

    public async Task<Outcome> CreateAsync(RegistrationRequest r, string? ip)
    {
        var name = r.StationName?.Trim() ?? "";
        if (name.Length is 0 or > 100) return Problem(400, "Station name must be 1..100 characters");
        var claimHash = r.ClaimHash?.Trim().ToLowerInvariant() ?? "";
        if (claimHash.Length != 64 || !claimHash.All(Uri.IsHexDigit)) return Problem(400, "claim_hash must be a hex SHA-256");

        await using var c = await db.OpenConnectionAsync();
        if (await BlockedUntilAsync(c, ip) is { } until) return Blocked(until);

        var existing = await c.QuerySingleOrDefaultAsync<Row>(Select + " WHERE claim_hash = @claimHash", new { claimHash });
        if (existing is not null)
            return existing.Status switch
            {
                "pending" or "approved" => new Outcome(200, Created(existing)),
                "rejected" => Rejected(),
                _ => Problem(410, "This request is closed; start a new one"),
            };

        var (byIp, total) = await c.QuerySingleAsync<(long, long)>(
            "SELECT count(*) FILTER (WHERE ip = @ip), count(*) FROM station_registrations WHERE status = 'pending'", new { ip });
        if (byIp >= MaxPendingPerIp || total >= MaxPendingTotal) return Problem(429, "Too many pending connection requests");

        var id = Guid.NewGuid();
        var code = RegistrationCode.Verification(claimHash);
        await c.ExecuteAsync("""
            INSERT INTO station_registrations (id, claim_hash, verification_code, station_name, machine_name, agent_version, os, rid, ip, topaz_info, previous_station_id)
            VALUES (@id, @claimHash, @code, @name, @machine, @version, @os, @rid, @ip, @topaz::jsonb, @prev)
            """, new
        {
            id, claimHash, code, name, ip,
            machine = Cut(r.MachineName, 100), version = Cut(r.AgentVersion, 40), os = Cut(r.Os, 200), rid = Cut(r.Rid, 32),
            topaz = r.Topaz is null ? null : System.Text.Json.JsonSerializer.Serialize(r.Topaz, Protocol.Json),
            prev = r.PreviousStationId,
        });
        log.LogInformation("Connection request {Id} for station {Station} from {Ip}", id, name, ip);
        detector.Wake(); // event in «Контроль» and Telegram right away
        return new Outcome(201, new RegistrationResponse { RegistrationId = id, VerificationCode = code, PollSeconds = PollSeconds });
    }

    /// <summary>
    /// The agent proves it owns the request with its secret. Once approved, every call issues a fresh token
    /// (a lost response is harmless) until the station first uses a token; then the request is closed.
    /// </summary>
    public async Task<Outcome> ClaimAsync(RegistrationClaimRequest r, string? ip)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var reg = await c.QuerySingleOrDefaultAsync<Row>(Select + " WHERE id = @id FOR UPDATE", new { id = r.RegistrationId }, tx);
        if (reg is null || !SameHash(reg.ClaimHash, RegistrationCode.ClaimHash(r.Secret ?? "")))
            return Problem(410, "Unknown connection request");
        if (await BlockedUntilAsync(c, ip) is { } until) return Blocked(until);
        await c.ExecuteAsync("UPDATE station_registrations SET last_poll_at = now(), poll_count = poll_count + 1, ip = COALESCE(@ip, ip) WHERE id = @id",
            new { id = reg.Id, ip }, tx);

        Outcome result;
        switch (reg.Status)
        {
            case "pending":
                result = new Outcome(202, new RegistrationClaimResponse { Status = RegistrationStatus.Pending, RetryAfterSeconds = PollSeconds });
                break;
            case "rejected":
                result = Rejected();
                break;
            case "approved" when reg.StationId is { } stationId:
                var token = Tokens.NewStationToken();
                var oldHash = await c.QuerySingleOrDefaultAsync<byte[]>(
                    "UPDATE stations s SET token_hash = @hash, token_issued_at = now(), status = 'active' FROM stations o WHERE s.id = o.id AND s.id = @stationId RETURNING o.token_hash",
                    new { stationId, hash = Tokens.Hash(token) }, tx);
                auth.Forget(oldHash); // a replaced PC loses access at once
                var name = await c.ExecuteScalarAsync<string>("SELECT name FROM stations WHERE id = @stationId", new { stationId }, tx);
                result = new Outcome(200, new RegistrationClaimResponse { Status = RegistrationStatus.Approved, StationId = stationId, StationName = name, Token = token });
                log.LogInformation("Connection request {Id}: token issued for station {Station}", reg.Id, name);
                break;
            default:
                result = Problem(410, "This request is closed; start a new one");
                break;
        }
        await tx.CommitAsync();
        return result;
    }

    // ---------------------------------------------------------------- administrator side

    public enum Decision { Ok, NotFound, NotPending, WrongCode, NameTaken, NoStation, NoOrganization }

    /// <summary>Approve as a new station (name) or as an existing one (reinstall / new PC, history kept).</summary>
    /// <param name="organizationId">For a new station; null is accepted only while there is exactly one organization.</param>
    public async Task<(Decision Result, Guid? StationId)> ApproveAsync(Guid id, string? code, Guid? existingStationId, string? newName, string by, Guid? organizationId = null)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var reg = await c.QuerySingleOrDefaultAsync<Row>(Select + " WHERE id = @id FOR UPDATE", new { id }, tx);
        if (reg is null) return (Decision.NotFound, null);
        if (reg.Status != "pending") return (Decision.NotPending, null);
        if (!RegistrationCode.Matches(reg.VerificationCode, code)) return (Decision.WrongCode, null);

        Guid stationId;
        if (existingStationId is { } existing)
        {
            if (!await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM stations WHERE id = @existing)", new { existing }, tx)) return (Decision.NoStation, null);
            stationId = existing;
        }
        else
        {
            var orgs = (await c.QueryAsync<Guid>("SELECT id FROM organizations WHERE is_active", transaction: tx)).ToList();
            var org = organizationId ?? (orgs.Count == 1 ? orgs[0] : (Guid?)null);
            if (org is null || !orgs.Contains(org.Value)) return (Decision.NoOrganization, null);
            var name = (string.IsNullOrWhiteSpace(newName) ? reg.StationName : newName).Trim();
            if (name.Length is 0 or > 100) return (Decision.NameTaken, null);
            if (await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM stations WHERE name_normalized = @n)", new { n = name.ToLowerInvariant() }, tx))
                return (Decision.NameTaken, null);
            stationId = Guid.NewGuid();
            await c.ExecuteAsync("""
                INSERT INTO stations (id, name, name_normalized, status, machine_name, agent_version, last_ip, organization_id)
                SELECT @stationId, @name, @norm, 'active', machine_name, agent_version, ip, @org FROM station_registrations WHERE id = @id
                """, new { stationId, name, norm = name.ToLowerInvariant(), id, org }, tx);
        }
        await c.ExecuteAsync("""
            UPDATE station_registrations SET status = 'approved', station_id = @stationId, decided_by = @by, decided_at = now() WHERE id = @id
            """, new { id, stationId, by }, tx);
        await tx.CommitAsync();
        log.LogInformation("Connection request {Id} approved by {By} as station {StationId}", id, by, stationId);
        return (Decision.Ok, stationId);
    }

    public async Task<Decision> RejectAsync(Guid id, bool blockIp, string? note, string by)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var reg = await c.QuerySingleOrDefaultAsync<Row>(Select + " WHERE id = @id FOR UPDATE", new { id }, tx);
        if (reg is null) return Decision.NotFound;
        if (reg.Status is not ("pending" or "approved")) return Decision.NotPending;
        await c.ExecuteAsync("UPDATE station_registrations SET status = 'rejected', decided_by = @by, decided_at = now(), note = @note WHERE id = @id",
            new { id, by, note = Cut(note, 500) }, tx);
        if (blockIp && !string.IsNullOrEmpty(reg.Ip))
            await c.ExecuteAsync("""
                INSERT INTO registration_blocks (ip, until, blocked_by, reason) VALUES (@ip, now() + @blockFor, @by, @note)
                ON CONFLICT (ip) DO UPDATE SET until = EXCLUDED.until, blocked_by = EXCLUDED.blocked_by, reason = EXCLUDED.reason
                """, new { ip = reg.Ip, blockFor = BlockFor, by, note = Cut(note, 500) }, tx);
        await tx.CommitAsync();
        return Decision.Ok;
    }

    /// <summary>block: the token stops working (kept, so unblock restores it); reissue: the token is dropped and the agent asks again.</summary>
    public async Task<bool> SetStationAccessAsync(Guid stationId, string action)
    {
        await using var c = await db.OpenConnectionAsync();
        var sql = action switch
        {
            "block" => "UPDATE stations s SET status = 'revoked' FROM stations o WHERE s.id = o.id AND s.id = @stationId RETURNING o.token_hash",
            "unblock" => "UPDATE stations s SET status = 'active' FROM stations o WHERE s.id = o.id AND s.id = @stationId AND o.token_hash IS NOT NULL RETURNING o.token_hash",
            "reissue" => "UPDATE stations s SET status = 'reenroll', token_hash = NULL FROM stations o WHERE s.id = o.id AND s.id = @stationId RETURNING o.token_hash",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        var rows = (await c.QueryAsync<byte[]?>(sql, new { stationId })).ToList();
        foreach (var h in rows) auth.Forget(h);
        return rows.Count > 0;
    }

    /// <summary>Housekeeping (every detection round): stale requests expire, IP blocks run out.</summary>
    public static Task MaintainAsync(NpgsqlConnection c) => c.ExecuteAsync("""
        UPDATE station_registrations SET status = 'expired'
        WHERE (status = 'pending' AND last_poll_at < now() - @after) OR (status = 'approved' AND decided_at < now() - @after);
        DELETE FROM registration_blocks WHERE until < now();
        """, new { after = ExpireAfter });

    /// <summary>Called when a station token is used for the first time: the request's secret stops working.</summary>
    public static Task MarkClaimedAsync(NpgsqlConnection c, Guid stationId) =>
        c.ExecuteAsync("UPDATE station_registrations SET status = 'claimed' WHERE station_id = @stationId AND status = 'approved'", new { stationId });

    // ---------------------------------------------------------------- helpers

    private const string Select = """
        SELECT id AS Id, claim_hash AS ClaimHash, verification_code AS VerificationCode, status AS Status, station_id AS StationId,
               station_name AS StationName, ip AS Ip
        FROM station_registrations
        """;

    private static RegistrationResponse Created(Row r) => new() { RegistrationId = r.Id, VerificationCode = r.VerificationCode, PollSeconds = PollSeconds };

    private static async Task<DateTime?> BlockedUntilAsync(NpgsqlConnection c, string? ip) => ip is null ? null
        : await c.QuerySingleOrDefaultAsync<DateTime?>("SELECT until FROM registration_blocks WHERE ip = @ip AND until > now()", new { ip });

    private static Outcome Blocked(DateTime until) => new(403, new RegistrationClaimResponse
    {
        Status = RegistrationStatus.Blocked, RetryAfterSeconds = (int)Math.Clamp((until - DateTime.UtcNow).TotalSeconds, 60, int.MaxValue),
    });

    private static Outcome Rejected() => new(403, new RegistrationClaimResponse { Status = RegistrationStatus.Rejected, RetryAfterSeconds = (int)RetryAfterReject.TotalSeconds });

    private static Outcome Problem(int status, string title) => new(status, new { title, status });

    private static bool SameHash(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    private static string? Cut(string? s, int max) => s is null ? null : s.Length <= max ? s.Trim() : s[..max].Trim();
}
