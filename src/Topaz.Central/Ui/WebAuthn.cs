using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Topaz.Central.Ui;

/// <summary>A passkey response that must be refused; the message is for the log, not for the user.</summary>
public sealed class WebAuthnException(string message) : Exception(message);

/// <summary>
/// The server side of WebAuthn (passkeys) for login: checks a new passkey (registration) and a login with it
/// (assertion). Only what login needs: attestation is not requested ("none"), so the attestation statement is
/// not checked; keys are ES256 (P-256) or RS256 — what phones, Macs and Windows Hello create.
/// User verification (fingerprint, face, device PIN) is required every time.
/// </summary>
public static class WebAuthn
{
    public const int Es256 = -7;
    public const int Rs256 = -257;
    public static readonly int[] Algorithms = [Es256, Rs256];

    private const byte FlagUserPresent = 0x01;
    private const byte FlagUserVerified = 0x04;
    private const byte FlagAttestedData = 0x40;

    public sealed record Registration(byte[] CredentialId, byte[] PublicKey, uint SignCount);

    /// <summary>Checks a new passkey; returns its id, public key (COSE) and counter.</summary>
    public static Registration VerifyRegistration(byte[] clientDataJson, byte[] attestationObject, string challenge, string origin, string rpId)
    {
        CheckClientData(clientDataJson, "webauthn.create", challenge, origin);
        var authData = ReadAuthData(attestationObject);
        var (flags, count) = CheckAuthData(authData, rpId);
        if ((flags & FlagAttestedData) == 0) throw new WebAuthnException("no credential data");

        var pos = 37 + 16; // rpIdHash, flags, counter, AAGUID
        if (authData.Length < pos + 2) throw new WebAuthnException("short credential data");
        var idLength = BinaryPrimitives.ReadUInt16BigEndian(authData.AsSpan(pos));
        pos += 2;
        if (idLength is 0 or > 1023 || authData.Length < pos + idLength) throw new WebAuthnException("bad credential id");
        var credentialId = authData.AsSpan(pos, idLength).ToArray();
        pos += idLength;

        var rest = authData.AsMemory(pos);
        var reader = new CborReader(rest, CborConformanceMode.Lax, allowMultipleRootLevelValues: true);
        reader.SkipValue();
        var publicKey = rest[..(rest.Length - reader.BytesRemaining)].ToArray();
        _ = ParseKey(publicKey); // a supported, valid key
        return new Registration(credentialId, publicKey, count);
    }

    /// <summary>Checks a login with a stored passkey; returns the new counter.</summary>
    public static uint VerifyAssertion(byte[] clientDataJson, byte[] authenticatorData, byte[] signature, byte[] publicKey,
        uint storedCount, string challenge, string origin, string rpId)
    {
        CheckClientData(clientDataJson, "webauthn.get", challenge, origin);
        var (_, count) = CheckAuthData(authenticatorData, rpId);
        var signed = authenticatorData.Concat(SHA256.HashData(clientDataJson)).ToArray();
        if (!VerifySignature(publicKey, signed, signature)) throw new WebAuthnException("bad signature");
        // A counter that does not grow means a copy of the key (devices that do not count always send 0).
        if ((count != 0 || storedCount != 0) && count <= storedCount) throw new WebAuthnException("counter did not grow");
        return count;
    }

    private static void CheckClientData(byte[] json, string type, string challenge, string origin)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (Str("type") != type) throw new WebAuthnException("wrong type");
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Str("challenge") ?? ""), Encoding.ASCII.GetBytes(challenge)))
            throw new WebAuthnException("wrong challenge");
        if (!string.Equals(Str("origin"), origin, StringComparison.Ordinal)) throw new WebAuthnException("wrong origin");
        if (root.TryGetProperty("crossOrigin", out var cross) && cross.ValueKind == JsonValueKind.True) throw new WebAuthnException("cross-origin");
    }

    private static (byte Flags, uint Count) CheckAuthData(byte[] authData, string rpId)
    {
        if (authData.Length < 37) throw new WebAuthnException("short authenticator data");
        if (!CryptographicOperations.FixedTimeEquals(authData.AsSpan(0, 32), SHA256.HashData(Encoding.UTF8.GetBytes(rpId))))
            throw new WebAuthnException("wrong relying party");
        var flags = authData[32];
        if ((flags & FlagUserPresent) == 0 || (flags & FlagUserVerified) == 0) throw new WebAuthnException("user not verified");
        return (flags, BinaryPrimitives.ReadUInt32BigEndian(authData.AsSpan(33)));
    }

    /// <summary>The authenticator data from an attestation object (CBOR map fmt / attStmt / authData).</summary>
    private static byte[] ReadAuthData(byte[] attestationObject)
    {
        try
        {
            var reader = new CborReader(attestationObject, CborConformanceMode.Lax);
            var n = reader.ReadStartMap();
            byte[]? authData = null;
            for (var i = 0; n is null ? reader.PeekState() != CborReaderState.EndMap : i < n; i++)
            {
                var key = reader.ReadTextString();
                if (key == "authData") authData = reader.ReadByteString();
                else reader.SkipValue();
            }
            return authData ?? throw new WebAuthnException("no authData");
        }
        catch (CborContentException)
        {
            throw new WebAuthnException("bad attestation object");
        }
        catch (InvalidOperationException)
        {
            throw new WebAuthnException("bad attestation object");
        }
    }

    private sealed record Key(int Alg, ECParameters? Ec, RSAParameters? Rsa);

    /// <summary>COSE_Key (RFC 9053): kty 2 / alg -7 / crv 1 (P-256), or kty 3 / alg -257.</summary>
    private static Key ParseKey(byte[] cose)
    {
        var fields = new Dictionary<long, object>();
        try
        {
            var reader = new CborReader(cose, CborConformanceMode.Lax);
            var n = reader.ReadStartMap();
            for (var i = 0; n is null ? reader.PeekState() != CborReaderState.EndMap : i < n; i++)
            {
                var label = reader.ReadInt64();
                fields[label] = reader.PeekState() switch
                {
                    CborReaderState.UnsignedInteger or CborReaderState.NegativeInteger => reader.ReadInt64(),
                    CborReaderState.ByteString => reader.ReadByteString(),
                    _ => Skip(reader),
                };
            }
        }
        catch (Exception e) when (e is CborContentException or InvalidOperationException or OverflowException)
        {
            throw new WebAuthnException("bad public key");
        }
        long? Int(long label) => fields.TryGetValue(label, out var v) && v is long l ? l : null;
        byte[]? Bytes(long label) => fields.TryGetValue(label, out var v) ? v as byte[] : null;

        var alg = Int(3);
        if (Int(1) == 2 && alg == Es256 && Int(-1) == 1 && Bytes(-2) is { Length: 32 } x && Bytes(-3) is { Length: 32 } y)
        {
            var p = new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y } };
            try { using var _ = ECDsa.Create(p); }
            catch (CryptographicException) { throw new WebAuthnException("point not on the curve"); }
            return new Key(Es256, p, null);
        }
        if (Int(1) == 3 && alg == Rs256 && Bytes(-1) is { Length: >= 256 } modulus && Bytes(-2) is { Length: > 0 } exponent)
            return new Key(Rs256, null, new RSAParameters { Modulus = modulus, Exponent = exponent });
        throw new WebAuthnException("unsupported key");
    }

    private static object Skip(CborReader reader)
    {
        reader.SkipValue();
        return "";
    }

    private static bool VerifySignature(byte[] cose, byte[] data, byte[] signature)
    {
        var key = ParseKey(cose);
        try
        {
            if (key.Ec is { } ec)
            {
                using var ecdsa = ECDsa.Create(ec);
                return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            }
            using var rsa = RSA.Create(key.Rsa!.Value);
            return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public static string ToBase64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[]? FromBase64Url(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 8192) return null;
        var b = s.Replace('-', '+').Replace('_', '/');
        b = b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=');
        try { return Convert.FromBase64String(b); }
        catch (FormatException) { return null; }
    }

    public static string NewChallenge() => ToBase64Url(RandomNumberGenerator.GetBytes(32));
}
