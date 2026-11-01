using System.Security.Cryptography;
using System.Text;
using Topaz.Agent.State;

namespace Topaz.Agent.Sending;

/// <summary>
/// Keeps the station token in local state, encrypted with Windows DPAPI (machine scope):
/// a copied state file is useless on another computer. On non-Windows (development only) the
/// token is stored unprotected and marked as such.
/// </summary>
public sealed class TokenStore(StateStore state)
{
    private const string TokenKey = "station_token";
    private const string StationIdKey = "station_id";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TopazAgent.StationToken.v1");

    public string? GetToken() => Unprotect(state.GetKv(TokenKey));

    public void Save(Guid stationId, string token)
    {
        state.SetKv(TokenKey, Protect(token));
        state.SetKv(StationIdKey, stationId.ToString());
    }

    /// <summary>DPAPI (machine scope) on Windows; marked plain text elsewhere (development only).</summary>
    internal static string Protect(string secret) => OperatingSystem.IsWindows()
        ? "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.LocalMachine))
        : "plain:" + secret;

    internal static string? Unprotect(string? stored)
    {
        if (stored is null) return null;
        if (stored.StartsWith("plain:", StringComparison.Ordinal)) return stored["plain:".Length..];
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored["dpapi:".Length..]), Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null; // state copied from another machine: a new connection request is needed
        }
    }

    public void Clear() => state.SetKv(TokenKey, null);

    public string? StationId => state.GetKv(StationIdKey);
}
