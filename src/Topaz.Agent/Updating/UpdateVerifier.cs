using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Topaz.Contracts;

namespace Topaz.Agent.Updating;

/// <summary>Accepts an update package only if CI signed its manifest and the zip matches it.</summary>
public sealed class UpdateVerifier(string publicKeyPem)
{
    public static string CurrentRid
    {
        get
        {
            var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : "osx";
            return $"{os}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
        }
    }

    /// <summary>Checks the signature and the fields; returns the manifest or throws <see cref="UpdateRejectedException"/>.</summary>
    public AgentUpdateManifest VerifyManifest(byte[] manifestJson, byte[] signature, string expectedVersion, string rid, string currentVersion)
    {
        using (var key = ECDsa.Create())
        {
            key.ImportFromPem(publicKeyPem);
            if (!key.VerifyData(manifestJson, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                throw new UpdateRejectedException("manifest signature is invalid");
        }
        AgentUpdateManifest m;
        try { m = JsonSerializer.Deserialize<AgentUpdateManifest>(manifestJson, Protocol.Json) ?? throw new JsonException("empty"); }
        catch (JsonException ex) { throw new UpdateRejectedException("manifest is not valid JSON: " + ex.Message); }
        if (m.Version != expectedVersion) throw new UpdateRejectedException($"manifest is for version {m.Version}, offered {expectedVersion}");
        if (!string.Equals(m.Rid, rid, StringComparison.OrdinalIgnoreCase)) throw new UpdateRejectedException($"package is for {m.Rid}, this agent is {rid}");
        if (SemVer.Compare(m.Version, currentVersion) <= 0) throw new UpdateRejectedException($"version {m.Version} is not newer than {currentVersion}");
        if (string.IsNullOrEmpty(m.File) || m.File != Path.GetFileName(m.File) || !m.File.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new UpdateRejectedException("manifest file name is invalid");
        if (m.Size <= 0 || m.Sha256.Length != 64) throw new UpdateRejectedException("manifest size or hash is invalid");
        return m;
    }

    public static void VerifyPackage(AgentUpdateManifest m, string zipPath)
    {
        var info = new FileInfo(zipPath);
        if (info.Length != m.Size) throw new UpdateRejectedException($"package size {info.Length} differs from manifest {m.Size}");
        using var f = File.OpenRead(zipPath);
        var hash = Convert.ToHexString(SHA256.HashData(f));
        if (!string.Equals(hash, m.Sha256, StringComparison.OrdinalIgnoreCase)) throw new UpdateRejectedException("package hash differs from manifest");
    }
}

/// <summary>The package must not be installed (bad signature, wrong version, corrupted download).</summary>
public sealed class UpdateRejectedException(string message) : Exception(message);
