using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Topaz.Central.Tests;

/// <summary>How the web UI is served so that an open tab survives a server update.</summary>
public sealed class WebHostingTests(CentralFixture fx) : IClassFixture<CentralFixture>, IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("topaz-wwwroot-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private WebApplicationFactory<Program> App()
    {
        File.WriteAllText(Path.Combine(_root, "index.html"), "<!doctype html><title>Topaz</title>");
        Directory.CreateDirectory(Path.Combine(_root, "assets"));
        File.WriteAllText(Path.Combine(_root, "assets", "index-AbC123.js"), "console.log(1)");
        return fx.Factory!.WithWebHostBuilder(b => b.UseWebRoot(_root));
    }

    [PostgresFact]
    public async Task Ui_answers_carry_the_server_version()
    {
        var res = await fx.Factory!.CreateClient().GetAsync("/api/v1/ui/auth/me");
        Assert.True(res.Headers.TryGetValues(AppVersion.Header, out var v));
        Assert.Equal(AppVersion.Current, Assert.Single(v!));
        Assert.DoesNotContain('+', AppVersion.Current);
    }

    [PostgresFact]
    public async Task Page_is_revalidated_and_hashed_assets_are_cached_for_good()
    {
        await using var app = App();
        var c = app.CreateClient();
        foreach (var path in new[] { "/", "/shifts/abc/1" })
        {
            var page = await c.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.True(page.Headers.CacheControl?.NoCache, path);
        }
        var asset = await c.GetAsync("/assets/index-AbC123.js");
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Contains("immutable", asset.Headers.CacheControl?.ToString());
    }

    [PostgresFact]
    public async Task A_missing_file_of_a_previous_release_is_404_not_the_page()
    {
        await using var app = App();
        var c = app.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/assets/ShiftCardPage-Old999.js")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/favicon-old.png")).StatusCode);
    }
}
