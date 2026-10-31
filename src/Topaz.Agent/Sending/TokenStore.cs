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

    public string? GetToken()
    {
        var stored = state.GetKv(TokenKey);
        if (stored is null) return null;
        if (stored.StartsWith("plain:", StringComparison.Ordinal)) return stored["plain:".Length..];
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored["dpapi:".Length..]), Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException)
        {
            return null; // state copied from another machine: re-enrollment required
        }
    }

    public void Save(Guid stationId, string token)
    {
        string stored;
        if (OperatingSystem.IsWindows())
            stored = "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.LocalMachine));
        else
            stored = "plain:" + token;
        state.SetKv(TokenKey, stored);
        state.SetKv(StationIdKey, stationId.ToString());
    }

    public void Clear() => state.SetKv(TokenKey, null);

    public string? StationId => state.GetKv(StationIdKey);
}
