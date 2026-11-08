using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Topaz.Central.Ui;
using Xunit;

namespace Topaz.Central.Tests;

public sealed class TotpUnitTests
{
    [Fact]
    public void Codes_match_rfc6238_test_vectors()
    {
        // RFC 6238 appendix B, SHA-1 secret; the last 6 of the 8 digits.
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        Assert.Equal("287082", Totp.Code(secret, Totp.Step(DateTimeOffset.FromUnixTimeSeconds(59))));
        Assert.Equal("081804", Totp.Code(secret, Totp.Step(DateTimeOffset.FromUnixTimeSeconds(1111111109))));
        Assert.Equal("050471", Totp.Code(secret, Totp.Step(DateTimeOffset.FromUnixTimeSeconds(1111111111))));
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Totp.Base32(secret));
    }

    [Fact]
    public void A_code_is_accepted_within_one_step_and_only_once()
    {
        var secret = Totp.NewSecret();
        var now = DateTimeOffset.UtcNow;
        var step = Totp.Step(now);
        Assert.Equal(step, Totp.Verify(secret, Totp.Code(secret, step), now, null));
        Assert.Equal(step - 1, Totp.Verify(secret, Totp.Code(secret, step - 1), now, null));
        Assert.Null(Totp.Verify(secret, Totp.Code(secret, step - 2), now, null));
        Assert.Null(Totp.Verify(secret, Totp.Code(secret, step), now, step)); // already used
        Assert.Null(Totp.Verify(secret, "12345", now, null));
        Assert.Null(Totp.Verify(secret, null, now, null));
    }

    [Fact]
    public void Recovery_codes_are_random_and_hashed_ignoring_case_and_spaces()
    {
        var codes = Totp.NewRecoveryCodes();
        Assert.Equal(Totp.RecoveryCodeCount, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches("^[a-z2-7]{5}-[a-z2-7]{5}$", c));
        Assert.Equal(Totp.HashRecoveryCode(codes[0]), Totp.HashRecoveryCode(" " + codes[0].ToUpperInvariant() + " "));
    }
}

public sealed class TotpLoginTests(CentralFixture fx) : IClassFixture<CentralFixture>
{
    private const string Password = "correct-horse-1";

    private HttpClient NewClient(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>? factory = null)
    {
        var c = (factory ?? fx.Factory!).CreateClient();
        c.DefaultRequestHeaders.Add(UiAuthSetup.CsrfHeader, "1");
        return c;
    }

    private static byte[] FromBase32(string s)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var ch in s)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(ch);
            bits += 5;
            if (bits >= 8) { bytes.Add((byte)(buffer >> (bits - 8))); bits -= 8; }
        }
        return [.. bytes];
    }

    /// <summary>Turns two-factor login on for a signed-in client; returns the secret and the recovery codes.</summary>
    private static async Task<(byte[] Secret, string[] Recovery)> EnableAsync(HttpClient c)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/v1/ui/auth/totp/setup", new { password = "wrong-password" })).StatusCode);
        var setup = await (await c.PostAsJsonAsync("/api/v1/ui/auth/totp/setup", new { password = Password })).Content.ReadFromJsonAsync<JsonObject>();
        var secret = FromBase32(setup!["secret"]!.GetValue<string>());
        Assert.StartsWith("otpauth://totp/", setup["uri"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/v1/ui/auth/totp/enable", new { code = "000000" })).StatusCode);
        var enabled = await c.PostAsJsonAsync("/api/v1/ui/auth/totp/enable", new { code = Totp.Code(secret, Totp.Step(DateTimeOffset.UtcNow)) });
        enabled.EnsureSuccessStatusCode();
        var recovery = (await enabled.Content.ReadFromJsonAsync<JsonObject>())!["recovery_codes"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        Assert.Equal(Totp.RecoveryCodeCount, recovery.Length);
        return (secret, recovery);
    }

    [PostgresFact]
    public async Task Login_with_two_factor_needs_a_fresh_code_or_a_recovery_code()
    {
        var users = fx.Factory!.Services.GetRequiredService<UserService>();
        await users.CreateAsync("totp-owner", "x", Roles.Owner, Password);
        var first = NewClient();
        (await first.PostAsJsonAsync("/api/v1/ui/auth/login", new { login = "totp-owner", password = Password })).EnsureSuccessStatusCode();
        var (secret, recovery) = await EnableAsync(first);
        var me = await first.GetFromJsonAsync<JsonObject>("/api/v1/ui/auth/me"); // the enabling session stays
        Assert.True(me!["totp_enabled"]!.GetValue<bool>());

        // Password alone: no session, only the code step.
        var c = NewClient();
        var login = await (await c.PostAsJsonAsync("/api/v1/ui/auth/login", new { login = "totp-owner", password = Password })).Content.ReadFromJsonAsync<JsonObject>();
        Assert.True(login!["need_code"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/ui/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/v1/ui/auth/login/code", new { code = "000000" })).StatusCode);
        // The code used to enable is spent; the next step's code works once.
        var used = Totp.Code(secret, Totp.Step(DateTimeOffset.UtcNow));
        var next = Totp.Code(secret, Totp.Step(DateTimeOffset.UtcNow) + 1);
        if (used != next) Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/v1/ui/auth/login/code", new { code = used })).StatusCode);
        (await c.PostAsJsonAsync("/api/v1/ui/auth/login/code", new { code = next })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/ui/auth/me")).StatusCode);

        // A code without the password step is refused.
        Assert.Equal(HttpStatusCode.Unauthorized, (await NewClient().PostAsJsonAsync("/api/v1/ui/auth/login/code", new { code = next })).StatusCode);

        // Recovery code: works once.
        var r = NewClient();
        (await r.PostAsJsonAsync("/api/v1/ui/auth/login", new { login = "totp-owner", password = Password })).EnsureSuccessStatusCode();
        (await r.PostAsJsonAsync("/api/v1/ui/auth/login/code", new { code = recovery[0].ToUpperInvariant() })).EnsureSuccessStatusCode();
        var r2 = NewClient();
        (await r2.PostAsJsonAsync("/api/v1/ui/auth/login", new { login = "totp-owner", password = Password })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await r2.PostAsJsonAsync("/api/v1/ui/auth/login/code", new { code = recovery[0] })).StatusCode);
        Assert.Equal(Totp.RecoveryCodeCount - 1, (await r.GetFromJsonAsync<JsonObject>("/api/v1/ui/auth/me"))!["recovery_codes_left"]!.GetValue<int>());

        // Guessing codes locks the account like wrong passwords; the password step does not reset the counter.
        var g = NewClient();
        var locked = false;
        for (var i = 0; i <= UserService.MaxFailedLogins && !locked; i++)
        {
            var step1 = await g.PostAsJsonAsync("/api/v1/ui/auth/login", new { login = "totp-owner", password = Password });
            locked = step1.StatusCode == (HttpStatusCode)423;
            if (locked) break;
            step1.EnsureSuccessStatusCode();
            var step2 = await g.PostAsJsonAsync("/api/v1/ui/auth/login/code", new { code = "000001" });
            Assert.Contains(step2.StatusCode, new[] { HttpStatusCode.Unauthorized, (HttpStatusCode)423 });
        }
        Assert.True(locked);
    }

    [PostgresFact]
    public async Task Owner_resets_a_users_two_factor_and_sessions_end()
    {
        var users = fx.Factory!.Services.GetRequiredService<UserService>();
        await users.CreateAsync("totp-boss", "x", Roles.Owner, Password);
        var id = await users.CreateAsync("totp-manager", "x", Roles.Manager, Password);
        var m = NewClient();
        (await m.PostAsJsonAsync("/api/v1/ui/auth/login", new { login = "totp-manager", password = Password })).EnsureSuccessStatusCode();
        await EnableAsync(m);
        var list = await LoginOwnerAsync("totp-boss");
        var row = (await list.GetFromJsonAsync<JsonArray>("/api/v1/ui/users"))!.First(u => u!["login"]!.GetValue<string>() == "totp-manager")!;
        Assert.True(row["totp_enabled"]!.GetValue<bool>());

        (await list.PatchAsJsonAsync($"/api/v1/ui/users/{id}", new { reset_totp = true })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await m.GetAsync("/api/v1/ui/auth/me")).StatusCode); // session ended
        var again = await (await NewClient().PostAsJsonAsync("/api/v1/ui/auth/login", new { login = "totp-manager", password = Password })).Content.ReadFromJsonAsync<JsonObject>();
        Assert.False(again!["need_code"]!.GetValue<bool>());
    }

    private async Task<HttpClient> LoginOwnerAsync(string login)
    {
        var c = NewClient();
        (await c.PostAsJsonAsync("/api/v1/ui/auth/login", new { login, password = Password })).EnsureSuccessStatusCode();
        return c;
    }

    [PostgresFact]
    public async Task A_superadmin_without_two_factor_can_only_set_it_up()
    {
        await using var strict = fx.Factory!.WithWebHostBuilder(b => b.UseSetting("Topaz:RequireTotpForSuperadmin", "true"));
        var users = strict.Services.GetRequiredService<UserService>();
        await users.CreateAsync("totp-super", "x", Roles.Superadmin, Password);
        var c = NewClient(strict);
        (await c.PostAsJsonAsync("/api/v1/ui/auth/login", new { login = "totp-super", password = Password })).EnsureSuccessStatusCode();
        var me = await c.GetFromJsonAsync<JsonObject>("/api/v1/ui/auth/me");
        Assert.True(me!["totp_setup_required"]!.GetValue<bool>());
        var blocked = await c.GetAsync("/api/v1/ui/meta");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Contains("totp_setup_required", await blocked.Content.ReadAsStringAsync());

        var (secret, _) = await EnableAsync(c);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/ui/meta")).StatusCode);
        Assert.False((await c.GetFromJsonAsync<JsonObject>("/api/v1/ui/auth/me"))!["totp_setup_required"]!.GetValue<bool>());
        // Mandatory: cannot be turned off by the superadmin.
        var off = await c.PostAsJsonAsync("/api/v1/ui/auth/totp/disable", new { password = Password, code = Totp.Code(secret, Totp.Step(DateTimeOffset.UtcNow) + 1) });
        Assert.Equal(HttpStatusCode.BadRequest, off.StatusCode);
    }
}
