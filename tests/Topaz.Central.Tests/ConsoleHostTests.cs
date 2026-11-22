using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Topaz.Central.Ui;
using Xunit;

namespace Topaz.Central.Tests;

/// <summary>
/// Topaz__ConsoleHost: a superadmin session exists only at the console address, every other session only outside it,
/// so a stolen client-side session is never a superadmin's (Ui/ConsoleHosts).
/// </summary>
public sealed class ConsoleHostTests(CentralFixture fx) : IClassFixture<CentralFixture>
{
    private const string Password = "console-password-1";
    private static readonly Uri Console = new("http://console.test");
    private static readonly Uri Client = new("http://localhost");

    private WebApplicationFactory<Program> App() => fx.Factory!.WithWebHostBuilder(b =>
    {
        b.UseSetting("Topaz:ConsoleHost", "console.test");
        b.UseSetting("Topaz:PublicOrigin", "https://topaz.example.ru");
    });

    private static async Task UserAsync(WebApplicationFactory<Program> app, string login, string role)
    {
        try { await app.Services.GetRequiredService<UserService>().CreateAsync(login, login, role, Password, []); }
        catch (Npgsql.PostgresException) { }
    }

    private static HttpClient At(WebApplicationFactory<Program> app, Uri address, bool cookies = true)
    {
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = address, HandleCookies = cookies });
        c.DefaultRequestHeaders.Add(UiAuthSetup.CsrfHeader, "1");
        return c;
    }

    private static Task<HttpResponseMessage> Login(HttpClient c, string login) =>
        c.PostAsJsonAsync("/api/v1/ui/auth/login", new { login, password = Password });

    [PostgresFact]
    public async Task A_superadmin_signs_in_only_at_the_console_and_owners_only_outside_it()
    {
        await using var app = App();
        await UserAsync(app, "con-super", Roles.Superadmin);
        await UserAsync(app, "con-owner", Roles.Owner);

        var refused = await Login(At(app, Client), "con-super");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("https://console.test", await refused.Content.ReadAsStringAsync()); // says where to go

        var console = At(app, Console);
        (await Login(console, "con-super")).EnsureSuccessStatusCode();
        var me = (await console.GetFromJsonAsync<JsonObject>("/api/v1/ui/auth/me"))!;
        Assert.True(me["console"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.OK, (await console.GetAsync("/api/v1/ui/organizations")).StatusCode);

        var owner = await Login(At(app, Console), "con-owner");
        Assert.Equal(HttpStatusCode.Forbidden, owner.StatusCode);
        Assert.Contains("https://topaz.example.ru", await owner.Content.ReadAsStringAsync());

        var client = At(app, Client);
        (await Login(client, "con-owner")).EnsureSuccessStatusCode();
        Assert.False((await client.GetFromJsonAsync<JsonObject>("/api/v1/ui/auth/me"))!["console"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/ui/organizations")).StatusCode);

        // The login screen learns where it is before anybody signs in.
        Assert.True((await At(app, Console).GetFromJsonAsync<JsonObject>("/api/v1/ui/auth/address"))!["console"]!.GetValue<bool>());
        Assert.False((await At(app, Client).GetFromJsonAsync<JsonObject>("/api/v1/ui/auth/address"))!["console"]!.GetValue<bool>());
    }

    [PostgresFact]
    public async Task A_session_carried_to_the_other_address_is_refused()
    {
        await using var app = App();
        await UserAsync(app, "con-super2", Roles.Superadmin);
        await UserAsync(app, "con-owner2", Roles.Owner);

        async Task<string> CookieOf(Uri at, string login)
        {
            var r = await Login(At(app, at, cookies: false), login);
            r.EnsureSuccessStatusCode();
            return string.Join("; ", r.Headers.GetValues("Set-Cookie").Select(v => v.Split(';')[0]));
        }
        async Task<HttpStatusCode> MeWith(Uri at, string cookie)
        {
            var c = At(app, at, cookies: false);
            c.DefaultRequestHeaders.Add("Cookie", cookie);
            return (await c.GetAsync("/api/v1/ui/auth/me")).StatusCode;
        }

        var superCookie = await CookieOf(Console, "con-super2");
        Assert.Equal(HttpStatusCode.OK, await MeWith(Console, superCookie));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeWith(Client, superCookie));

        var ownerCookie = await CookieOf(Client, "con-owner2");
        Assert.Equal(HttpStatusCode.OK, await MeWith(Client, ownerCookie));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeWith(Console, ownerCookie));
    }

    [PostgresFact]
    public async Task Console_passkeys_belong_to_the_console_address()
    {
        await using var app = App();
        async Task<string> RpId(Uri at)
        {
            var r = await At(app, at).PostAsync("/api/v1/ui/auth/passkey/options", null);
            r.EnsureSuccessStatusCode();
            return (await r.Content.ReadFromJsonAsync<JsonObject>())!["rpId"]!.GetValue<string>();
        }
        Assert.Equal("console.test", await RpId(Console));
        Assert.Equal("topaz.example.ru", await RpId(Client));
    }
}
