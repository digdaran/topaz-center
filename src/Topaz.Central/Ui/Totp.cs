using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace Topaz.Central.Ui;

/// <summary>
/// Time-based one-time passwords (RFC 6238: HMAC-SHA1, 6 digits, 30 s), as in Яндекс Ключ / Google Authenticator,
/// plus one-time recovery codes. Secrets are stored encrypted (<see cref="TotpProtector"/>), recovery codes as hashes.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int PeriodSeconds = 30;
    /// <summary>Accepted clock drift: the previous and the next 30-second step.</summary>
    public const int Window = 1;
    public const int RecoveryCodeCount = 8;
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static long Step(DateTimeOffset at) => at.ToUnixTimeSeconds() / PeriodSeconds;

    public static string Code(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        for (var i = 7; i >= 0; i--, step >>= 8) counter[i] = (byte)(step & 0xff);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);
        var offset = hash[^1] & 0x0f;
        var value = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % 1_000_000).ToString("D6");
    }

    /// <summary>
    /// The step the code belongs to, or null. A step not later than <paramref name="lastUsedStep"/> is refused,
    /// so a code cannot be used twice.
    /// </summary>
    public static long? Verify(byte[] secret, string? code, DateTimeOffset now, long? lastUsedStep)
    {
        code = Normalize(code);
        if (code.Length != Digits || !code.All(char.IsAsciiDigit)) return null;
        var current = Step(now);
        for (var s = current - Window; s <= current + Window; s++)
        {
            if (lastUsedStep is { } last && s <= last) continue;
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, s)), Encoding.ASCII.GetBytes(code)))
                return s;
        }
        return null;
    }

    /// <summary>otpauth:// link for the QR code an authenticator app scans.</summary>
    public static string Uri(byte[] secret, string issuer, string account) =>
        $"otpauth://totp/{System.Uri.EscapeDataString(issuer)}:{System.Uri.EscapeDataString(account)}" +
        $"?secret={Base32(secret)}&issuer={System.Uri.EscapeDataString(issuer)}&digits={Digits}&period={PeriodSeconds}";

    public static string Base32(byte[] data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    /// <summary>Codes like "k3x9p-2mq7d": 50 random bits each, shown once, stored as SHA-256.</summary>
    public static string[] NewRecoveryCodes() =>
        Enumerable.Range(0, RecoveryCodeCount).Select(_ =>
        {
            var s = Base32(RandomNumberGenerator.GetBytes(7)).ToLowerInvariant()[..10];
            return s[..5] + "-" + s[5..];
        }).ToArray();

    public static string HashRecoveryCode(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(code).Replace("-", "").ToLowerInvariant())));

    private static string Normalize(string? code) => (code ?? "").Replace(" ", "").Trim();
}

/// <summary>Encrypts TOTP secrets with the app's data protection keys (Docker volume "keys").</summary>
public sealed class TotpProtector(IDataProtectionProvider provider)
{
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(5);

    private readonly IDataProtector _secrets = provider.CreateProtector("Topaz.Central.Totp.Secret.v1");
    private readonly ITimeLimitedDataProtector _tickets = provider.CreateProtector("Topaz.Central.Totp.Ticket.v1").ToTimeLimitedDataProtector();

    public string Protect(byte[] secret) => Convert.ToBase64String(_secrets.Protect(secret));
    public byte[] Unprotect(string stored) => _secrets.Unprotect(Convert.FromBase64String(stored));

    /// <summary>Proof that the password step passed: user id + security stamp, valid for 5 minutes.</summary>
    public string Ticket(Guid userId, Guid stamp) => _tickets.Protect($"{userId:N}|{stamp:N}", TicketLifetime);

    public (Guid UserId, Guid Stamp)? ReadTicket(string? ticket)
    {
        if (string.IsNullOrEmpty(ticket)) return null;
        try
        {
            var parts = _tickets.Unprotect(ticket, out _).Split('|');
            return parts.Length == 2 && Guid.TryParse(parts[0], out var u) && Guid.TryParse(parts[1], out var s) ? (u, s) : null;
        }
        catch (CryptographicException)
        {
            return null; // expired or forged
        }
    }
}
