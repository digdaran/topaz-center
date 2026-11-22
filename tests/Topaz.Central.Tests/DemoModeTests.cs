using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Topaz.Central.Ui;
using Topaz.Contracts;
using Xunit;

namespace Topaz.Central.Tests;

/// <summary>Topaz__DemoMode (the public demo): a ready visitor account, everything readable, nothing changeable.</summary>
public sealed class DemoModeTests(CentralFixture fx) : IClassFixture<CentralFixture>
{
    private WebApplicationFactory<Program> Demo() => fx.Factory!.WithWebHostBuilder(b =>
    {
        b.UseSetting("Topaz:DemoMode", "true");
        b.UseSetting("Topaz:DemoLogin", "demo");
        b.UseSetting("Topaz:DemoPassword", "demo-password-1");
    });

    private static HttpClient Ui(WebApplicationFactory<Program> app)
    {
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Add(UiAuthSetup.CsrfHeader, "1");
        return c;
    }

    [PostgresFact]
    public async Task A_visitor_signs_in_with_the_offered_account_sees_everything_and_changes_nothing()
    {
        await using var app = Demo();
        var c = Ui(app);
        var address = (await c.GetFromJsonAsync<JsonObject>("/api/v1/ui/auth/address"))!;
        Assert.True(address["demo"]!.GetValue<bool>());
        var login = address["demo_login"]!.GetValue<string>();
        var password = address["demo_password"]!.GetValue<string>();

        (await c.PostAsJsonAsync("/api/v1/ui/auth/login", new { login, password })).EnsureSuccessStatusCode();
        var me = (await c.GetFromJsonAsync<JsonObject>("/api/v1/ui/auth/me"))!;
        Assert.Equal("owner", me["role"]!.GetValue<string>());
        Assert.True(me["demo"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/ui/analytics/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/ui/costs/fuel")).StatusCode);

        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Post, "/api/v1/ui/costs/fuel", new { station_id = Guid.NewGuid(), fuel_id = 1, valid_from = DateTimeOffset.UtcNow, price_per_liter = 50 }),
            (HttpMethod.Post, "/api/v1/ui/users", new { login = "x-user", name = "x", role = "viewer", password = "x-password-123" }),
            (HttpMethod.Post, "/api/v1/ui/auth/password", new { current_password = password, new_password = "another-password-1" }),
            (HttpMethod.Post, "/api/v1/ui/auth/totp/setup", new { password }),
            (HttpMethod.Delete, "/api/v1/ui/auth/passkeys/x", null),
        })
        {
            var r = await c.SendAsync(new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) });
            Assert.True(r.StatusCode == HttpStatusCode.Forbidden, $"{method} {path}: {r.StatusCode}");
            Assert.Contains("демо", await r.Content.ReadAsStringAsync());
        }
        // The account still works with the same password, and signing out is allowed.
        Assert.True((await c.PostAsync("/api/v1/ui/auth/logout", null)).IsSuccessStatusCode);
        (await Ui(app).PostAsJsonAsync("/api/v1/ui/auth/login", new { login, password })).EnsureSuccessStatusCode();
    }

    [PostgresFact]
    public async Task The_demo_still_receives_data_and_an_ordinary_server_offers_no_account()
    {
        await using var app = Demo();
        // demo-feeder connects stations through the agent protocol and the admin API, which the demo leaves alone.
        var station = await fx.EnrollAsync("АЗС-демо-приём");
        var agent = app.CreateClient();
        agent.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", station.Token);
        Assert.Equal(HttpStatusCode.OK, (await agent.PostAsJsonAsync("/api/v1/heartbeat", new HeartbeatRequest { AgentVersion = "demo" }, Protocol.Json)).StatusCode);

        var ordinary = (await Ui(fx.Factory!).GetFromJsonAsync<JsonObject>("/api/v1/ui/auth/address"))!;
        Assert.False(ordinary["demo"]!.GetValue<bool>());
        Assert.Null(ordinary["demo_login"]);
    }
}
