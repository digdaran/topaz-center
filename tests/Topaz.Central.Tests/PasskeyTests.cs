using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Topaz.Central.Ui;
using Xunit;

namespace Topaz.Central.Tests;

/// <summary>
/// A software passkey: does what a phone or Windows Hello does — makes a key pair for the site and signs login
/// challenges after "biometrics" (the user-verified flag).
/// </summary>
internal sealed class SoftAuthenticator(string origin, string rpId, bool rsa = false)
{
    private readonly ECDsa _ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly RSA _rsa = RSA.Create(2048);
    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(16);
    public byte[] UserHandle { get; private set; } = [];
    public uint Counter { get; set; }
    public byte Flags { get; set; } = 0x05; // user present + user verified
    public string Origin { get; set; } = origin;
    public string RpId { get; set; } = rpId;

    public byte[] CoseKey()
    {
        var w = new CborWriter(CborConformanceMode.Ctap2Canonical);
        if (rsa)
        {
            var p = _rsa.ExportParameters(false);
            w.WriteStartMap(4);
            w.WriteInt32(1); w.WriteInt32(3);
            w.WriteInt32(3); w.WriteInt32(WebAuthn.Rs256);
            w.WriteInt32(-1); w.WriteByteString(p.Modulus!);
            w.WriteInt32(-2); w.WriteByteString(p.Exponent!);
        }
        else
        {
            var p = _ec.ExportParameters(false);
            w.WriteStartMap(5);
            w.WriteInt32(1); w.WriteInt32(2);
            w.WriteInt32(3); w.WriteInt32(WebAuthn.Es256);
            w.WriteInt32(-1); w.WriteInt32(1);
            w.WriteInt32(-2); w.WriteByteString(p.Q.X!);
            w.WriteInt32(-3); w.WriteByteString(p.Q.Y!);
        }
        w.WriteEndMap();
        return w.Encode();
    }

    private byte[] AuthData(bool attested, byte[]? coseKey = null)
    {
        var data = new List<byte>(SHA256.HashData(Encoding.UTF8.GetBytes(RpId)));
        data.Add((byte)(Flags | (attested ? 0x40 : 0)));
        var counter = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(counter, Counter);
        data.AddRange(counter);
        if (attested)
        {
            data.AddRange(new byte[16]); // AAGUID
            data.Add((byte)(CredentialId.Length >> 8));
            data.Add((byte)CredentialId.Length);
            data.AddRange(CredentialId);
            data.AddRange(coseKey ?? CoseKey());
        }
        return [.. data];
    }

    private byte[] ClientData(string type, string challenge) =>
        Encoding.UTF8.GetBytes(new JsonObject { ["type"] = type, ["challenge"] = challenge, ["origin"] = Origin, ["crossOrigin"] = false }.ToJsonString());

    public (byte[] ClientData, byte[] Attestation) MakeRaw(string challenge, byte[]? coseKey = null)
    {
        var w = new CborWriter(CborConformanceMode.Lax);
        w.WriteStartMap(3);
        w.WriteTextString("fmt"); w.WriteTextString("none");
        w.WriteTextString("attStmt"); w.WriteStartMap(0); w.WriteEndMap();
        w.WriteTextString("authData"); w.WriteByteString(AuthData(true, coseKey));
        w.WriteEndMap();
        return (ClientData("webauthn.create", challenge), w.Encode());
    }

    public (byte[] ClientData, byte[] AuthData, byte[] Signature) SignRaw(string challenge)
    {
        Counter++;
        var authData = AuthData(false);
        var clientData = ClientData("webauthn.get", challenge);
        var signed = authData.Concat(SHA256.HashData(clientData)).ToArray();
        var signature = rsa
            ? _rsa.SignData(signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            : _ec.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return (clientData, authData, signature);
    }

    private static string B64(byte[] b) => WebAuthn.ToBase64Url(b);

    /// <summary>navigator.credentials.create() for the server's options, serialized like the web page does.</summary>
    public JsonObject Create(JsonObject options)
    {
        UserHandle = WebAuthn.FromBase64Url(options["user"]!["id"]!.GetValue<string>())!;
        var (clientData, attestation) = MakeRaw(options["challenge"]!.GetValue<string>());
        return new JsonObject
        {
            ["id"] = B64(CredentialId), ["rawId"] = B64(CredentialId), ["type"] = "public-key",
            ["response"] = new JsonObject { ["clientDataJSON"] = B64(clientData), ["attestationObject"] = B64(attestation) },
        };
    }

    /// <summary>navigator.credentials.get(): the device picks its passkey for the site and signs.</summary>
    public JsonObject Get(JsonObject options)
    {
        var (clientData, authData, signature) = SignRaw(options["challenge"]!.GetValue<string>());
        return new JsonObject
        {
            ["id"] = B64(CredentialId), ["rawId"] = B64(CredentialId), ["type"] = "public-key",
            ["response"] = new JsonObject
            {
                ["clientDataJSON"] = B64(clientData), ["authenticatorData"] = B64(authData),
                ["signature"] = B64(signature), ["userHandle"] = B64(UserHandle),
            },
        };
    }
}

public sealed class WebAuthnUnitTests
{
    private const string Origin = "https://topaz.example.ru";
    private const string RpId = "topaz.example.ru";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_passkey_registers_and_signs_logins_es256_and_rs256(bool rsa)
    {
        var device = new SoftAuthenticator(Origin, RpId, rsa);
        var challenge = WebAuthn.NewChallenge();
        var (clientData, attestation) = device.MakeRaw(challenge);
        var reg = WebAuthn.VerifyRegistration(clientData, attestation, challenge, Origin, RpId);
        Assert.Equal(device.CredentialId, reg.CredentialId);
        Assert.Equal(device.CoseKey(), reg.PublicKey);

        var login = WebAuthn.NewChallenge();
        var (cd, ad, sig) = device.SignRaw(login);
        Assert.Equal(1u, WebAuthn.VerifyAssertion(cd, ad, sig, reg.PublicKey, 0, login, Origin, RpId));
    }

    [Fact]
    public void Wrong_site_challenge_flags_signature_or_counter_are_refused()
    {
        var device = new SoftAuthenticator(Origin, RpId);
        var challenge = WebAuthn.NewChallenge();
        var (clientData, attestation) = device.MakeRaw(challenge);
        var key = WebAuthn.VerifyRegistration(clientData, attestation, challenge, Origin, RpId).PublicKey;

        Assert.Throws<WebAuthnException>(() => WebAuthn.VerifyRegistration(clientData, attestation, WebAuthn.NewChallenge(), Origin, RpId));
        Assert.Throws<WebAuthnException>(() => WebAuthn.VerifyRegistration(clientData, attestation, challenge, "https://evil.example", RpId));
        Assert.Throws<WebAuthnException>(() => WebAuthn.VerifyRegistration(clientData, attestation, challenge, Origin, "evil.example"));

        var login = WebAuthn.NewChallenge();
        var (cd, ad, sig) = device.SignRaw(login);
        Assert.Throws<WebAuthnException>(() => WebAuthn.VerifyAssertion(cd, ad, sig, key, 0, WebAuthn.NewChallenge(), Origin, RpId));
        Assert.Throws<WebAuthnException>(() => WebAuthn.VerifyAssertion(cd, ad, sig, key, 0, login, "https://evil.example", RpId));
        // A registration response is not a login response.
        Assert.Throws<WebAuthnException>(() => WebAuthn.VerifyAssertion(clientData, ad, sig, key, 0, challenge, Origin, RpId));
        var forged = sig.ToArray();
        forged[^1] ^= 1;
        Assert.Throws<WebAuthnException>(() => WebAuthn.VerifyAssertion(cd, ad, forged, key, 0, login, Origin, RpId));
        // Another device's signature does not match the stored key.
        var other = new SoftAuthenticator(Origin, RpId);
        var (ocd, oad, osig) = other.SignRaw(login);
        Assert.Throws<WebAuthnException>(() => WebAuthn.VerifyAssertion(ocd, oad, osig, key, 0, login, Origin, RpId));
        // A copied key: the counter did not grow.
        Assert.Throws<WebAuthnException>(() => WebAuthn.VerifyAssertion(cd, ad, sig, key, 5, login, Origin, RpId));
        // Devices that do not count (always 0) are fine.
        device.Counter = uint.MaxValue; // next SignRaw wraps to 0
        var (zcd, zad, zsig) = device.SignRaw(login);
        Assert.Equal(0u, WebAuthn.VerifyAssertion(zcd, zad, zsig, key, 0, login, Origin, RpId));

        // Without user verification (no fingerprint/face/PIN): refused.
        device.Flags = 0x01;
        var (ncd, nad, nsig) = device.SignRaw(login);
        Assert.Throws<WebAuthnException>(() => WebAuthn.VerifyAssertion(ncd, nad, nsig, key, 0, login, Origin, RpId));
        var (rcd, ratt) = device.MakeRaw(challenge);
        Assert.Throws<WebAuthnException>(() => WebAuthn.VerifyRegistration(rcd, ratt, challenge, Origin, RpId));
    }

    [Fact]
    public void Unsupported_or_broken_keys_are_refused()
    {
        var device = new SoftAuthenticator(Origin, RpId);
        var challenge = WebAuthn.NewChallenge();
        var w = new CborWriter(CborConformanceMode.Ctap2Canonical);
        w.WriteStartMap(4);
        w.WriteInt32(1); w.WriteInt32(1); // OKP
        w.WriteInt32(3); w.WriteInt32(-8); // EdDSA
        w.WriteInt32(-1); w.WriteInt32(6);
        w.WriteInt32(-2); w.WriteByteString(new byte[32]);
        w.WriteEndMap();
        var (cd, att) = device.MakeRaw(challenge, w.Encode());
        Assert.Throws<WebAuthnException>(() => WebAuthn.VerifyRegistration(cd, att, challenge, Origin, RpId));

        var bad = new CborWriter(CborConformanceMode.Ctap2Canonical);
        bad.WriteStartMap(5);
        bad.WriteInt32(1); bad.WriteInt32(2);
        bad.WriteInt32(3); bad.WriteInt32(WebAuthn.Es256);
        bad.WriteInt32(-1); bad.WriteInt32(1);
        bad.WriteInt32(-2); bad.WriteByteString(new byte[32]); // not a point on the curve
        bad.WriteInt32(-3); bad.WriteByteString(new byte[32]);
        bad.WriteEndMap();
        (cd, att) = device.MakeRaw(challenge, bad.Encode());
        Assert.Throws<WebAuthnException>(() => WebAuthn.VerifyRegistration(cd, att, challenge, Origin, RpId));
        Assert.Throws<WebAuthnException>(() => WebAuthn.VerifyRegistration(cd, [0xa1, 0x01], challenge, Origin, RpId));
    }

    [Fact]
    public void Base64url_round_trips_and_rejects_garbage()
    {
        var bytes = RandomNumberGenerator.GetBytes(33);
        var s = WebAuthn.ToBase64Url(bytes);
        Assert.DoesNotContain('=', s);
        Assert.Equal(bytes, WebAuthn.FromBase64Url(s));
        Assert.Null(WebAuthn.FromBase64Url("!!"));
        Assert.Null(WebAuthn.FromBase64Url(null));
    }

    [Theory]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X)", "iPhone")]
    [InlineData("Mozilla/5.0 (Linux; Android 14; SM-S918B)", "Android")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64)", "Windows")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 14_0)", "Mac")]
    public void Default_passkey_name_is_the_device(string ua, string name) => Assert.Equal(name, PasskeyEndpoints.DeviceName(ua));
}

public sealed class PasskeyLoginTests(CentralFixture fx) : IClassFixture<CentralFixture>
{
    private const string Password = "correct-horse-1";
    // TestServer: the browser address is http://localhost.
    private const string Origin = "http://localhost";
    private const string RpId = "localhost";

    private HttpClient NewClient()
    {
        var c = fx.Factory!.CreateClient();
        c.DefaultRequestHeaders.Add(UiAuthSetup.CsrfHeader, "1");
        return c;
    }

    private async Task<HttpClient> LoginAsync(string login)
    {
        var c = NewClient();
        (await c.PostAsJsonAsync("/api/v1/ui/auth/login", new { login, password = Password })).EnsureSuccessStatusCode();
        return c;
    }

    private static async Task<SoftAuthenticator> AddPasskeyAsync(HttpClient c, string? name = null)
    {
        var device = new SoftAuthenticator(Origin, RpId);
        var options = await (await c.PostAsJsonAsync("/api/v1/ui/auth/passkeys/options", new { password = Password })).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("required", options!["authenticatorSelection"]!["userVerification"]!.GetValue<string>());
        Assert.Equal(RpId, options["rp"]!["id"]!.GetValue<string>());
        var added = await c.PostAsJsonAsync("/api/v1/ui/auth/passkeys", new { credential = device.Create(options), name });
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
        return device;
    }

    private async Task<HttpResponseMessage> PasskeyLoginAsync(HttpClient c, SoftAuthenticator device)
    {
        var options = await (await c.PostAsJsonAsync("/api/v1/ui/auth/passkey/options", new { })).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonObject>();
        return await c.PostAsJsonAsync("/api/v1/ui/auth/passkey/login", device.Get(options!));
    }

    [PostgresFact]
    public async Task Sessions_last_a_week_and_survive_closing_the_browser()
    {
        var users = fx.Factory!.Services.GetRequiredService<UserService>();
        await users.CreateAsync("pk-session", "x", Roles.Owner, Password);
        var r = await NewClient().PostAsJsonAsync("/api/v1/ui/auth/login", new { login = "pk-session", password = Password });
        r.EnsureSuccessStatusCode();
        var cookie = r.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith(UiAuthSetup.CookieName + "="));
        var expires = DateTimeOffset.Parse(System.Text.RegularExpressions.Regex.Match(cookie, "expires=([^;]+)").Groups[1].Value);
        Assert.InRange(expires - DateTimeOffset.UtcNow, TimeSpan.FromDays(6.9), TimeSpan.FromDays(7.1));
    }

    [PostgresFact]
    public async Task A_passkey_logs_in_without_a_password_once_per_challenge()
    {
        var users = fx.Factory!.Services.GetRequiredService<UserService>();
        await users.CreateAsync("pk-owner", "Владелец", Roles.Owner, Password);
        var c = await LoginAsync("pk-owner");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/v1/ui/auth/passkeys/options", new { password = "wrong-password" })).StatusCode);
        // Without the options step (no challenge) nothing is added.
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/v1/ui/auth/passkeys",
            new { credential = new SoftAuthenticator(Origin, RpId).Create(new JsonObject { ["challenge"] = "x", ["user"] = new JsonObject { ["id"] = "AA" } }) })).StatusCode);
        var device = await AddPasskeyAsync(c, "Мой телефон");
        var list = await c.GetFromJsonAsync<JsonArray>("/api/v1/ui/auth/passkeys");
        Assert.Equal("Мой телефон", list!.Single()!["name"]!.GetValue<string>());
        // The same device cannot be added twice.
        var again = await (await c.PostAsJsonAsync("/api/v1/ui/auth/passkeys/options", new { password = Password })).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(WebAuthn.ToBase64Url(device.CredentialId), again!["excludeCredentials"]![0]!["id"]!.GetValue<string>());

        var p = NewClient();
        (await PasskeyLoginAsync(p, device)).EnsureSuccessStatusCode();
        var me = await p.GetFromJsonAsync<JsonObject>("/api/v1/ui/auth/me");
        Assert.Equal("pk-owner", me!["login"]!.GetValue<string>());
        Assert.NotNull((await p.GetFromJsonAsync<JsonArray>("/api/v1/ui/auth/passkeys"))!.Single()!["last_used_at"]);

        // A captured response cannot be replayed, neither without the challenge cookie nor with a new challenge.
        var x = NewClient();
        var options = await (await x.PostAsJsonAsync("/api/v1/ui/auth/passkey/options", new { })).Content.ReadFromJsonAsync<JsonObject>();
        var signed = device.Get(options!);
        (await x.PostAsJsonAsync("/api/v1/ui/auth/passkey/login", signed)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await NewClient().PostAsJsonAsync("/api/v1/ui/auth/passkey/login", signed)).StatusCode);
        var y = NewClient();
        await y.PostAsJsonAsync("/api/v1/ui/auth/passkey/options", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, (await y.PostAsJsonAsync("/api/v1/ui/auth/passkey/login", signed)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await y.GetAsync("/api/v1/ui/auth/me")).StatusCode);

        // A device that is not registered, or a site that is not ours.
        Assert.Equal(HttpStatusCode.Unauthorized, (await PasskeyLoginAsync(NewClient(), new SoftAuthenticator(Origin, RpId))).StatusCode);
        device.Origin = "http://evil.localhost";
        Assert.Equal(HttpStatusCode.Unauthorized, (await PasskeyLoginAsync(NewClient(), device)).StatusCode);
    }

    [PostgresFact]
    public async Task Removing_a_passkey_or_a_reset_ends_its_logins_and_the_sessions()
    {
        var users = fx.Factory!.Services.GetRequiredService<UserService>();
        await users.CreateAsync("pk-boss", "x", Roles.Owner, Password);
        var id = await users.CreateAsync("pk-manager", "x", Roles.Manager, Password);
        var c = await LoginAsync("pk-manager");
        var phone = await AddPasskeyAsync(c);
        var laptop = await AddPasskeyAsync(c);
        var onPhone = NewClient();
        (await PasskeyLoginAsync(onPhone, phone)).EnsureSuccessStatusCode();

        // The phone is lost: its passkey is removed; the phone's session ends, this one stays.
        (await c.DeleteAsync("/api/v1/ui/auth/passkeys/" + WebAuthn.ToBase64Url(phone.CredentialId))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await onPhone.GetAsync("/api/v1/ui/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/ui/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PasskeyLoginAsync(NewClient(), phone)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync("/api/v1/ui/auth/passkeys/" + WebAuthn.ToBase64Url(phone.CredentialId))).StatusCode);

        // The owner sees the passkeys and resets them with the code.
        var boss = await LoginAsync("pk-boss");
        var row = (await boss.GetFromJsonAsync<JsonArray>("/api/v1/ui/users"))!.First(u => u!["login"]!.GetValue<string>() == "pk-manager")!;
        Assert.Equal(1, row["passkeys"]!.GetValue<int>());
        (await PasskeyLoginAsync(NewClient(), laptop)).EnsureSuccessStatusCode();
        (await boss.PatchAsJsonAsync($"/api/v1/ui/users/{id}", new { reset_totp = true })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await PasskeyLoginAsync(NewClient(), laptop)).StatusCode);

        // A blocked user cannot log in with a passkey.
        var c2 = await LoginAsync("pk-manager");
        var fresh = await AddPasskeyAsync(c2);
        (await boss.PatchAsJsonAsync($"/api/v1/ui/users/{id}", new { is_active = false })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await PasskeyLoginAsync(NewClient(), fresh)).StatusCode);
    }

    [PostgresFact]
    public async Task Someone_else_cannot_remove_my_passkey()
    {
        var users = fx.Factory!.Services.GetRequiredService<UserService>();
        await users.CreateAsync("pk-alice", "x", Roles.Owner, Password);
        await users.CreateAsync("pk-bob", "x", Roles.Owner, Password);
        var alice = await LoginAsync("pk-alice");
        var device = await AddPasskeyAsync(alice);
        var bob = await LoginAsync("pk-bob");
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync("/api/v1/ui/auth/passkeys/" + WebAuthn.ToBase64Url(device.CredentialId))).StatusCode);
        Assert.Empty((await bob.GetFromJsonAsync<JsonArray>("/api/v1/ui/auth/passkeys"))!);
        (await PasskeyLoginAsync(NewClient(), device)).EnsureSuccessStatusCode();
    }
}
