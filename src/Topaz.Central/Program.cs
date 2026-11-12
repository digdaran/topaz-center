using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Prometheus;
using Serilog;
using Serilog.Formatting.Compact;
using Topaz.Central;
using Topaz.Central.Auth;
using Topaz.Central.Data;
using Topaz.Central.Endpoints;
using Topaz.Central.Monitoring;
using Topaz.Central.Processing;
using Topaz.Central.Ui;
using Microsoft.AspNetCore.DataProtection;
using Topaz.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(new RenderedCompactJsonFormatter()));

builder.Services.Configure<CentralOptions>(builder.Configuration.GetSection("Topaz"));
var options = builder.Configuration.GetSection("Topaz").Get<CentralOptions>() ?? new CentralOptions();

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings__Postgres is not set");
// Background work, ingestion, admin API: the system pool. The web UI uses its own pool restricted by row level security.
builder.Services.AddSingleton(_ => new NpgsqlDataSourceBuilder(
    new NpgsqlConnectionStringBuilder(connectionString) { Options = "-c app.scope=system" }.ConnectionString).Build());
builder.Services.AddSingleton(_ => new UiDataSource(UiDb.BuildUiSource(connectionString)));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<UiDb>();

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<StationAuthenticator>();
builder.Services.AddSingleton<StationAuthFilter>();
builder.Services.AddSingleton<Registrations>();
builder.Services.AddSingleton<AdminAuthFilter>();
builder.Services.AddSingleton<BatchSignal>();
builder.Services.AddSingleton<Migrator>();
builder.Services.AddSingleton<Topaz.Central.Updates.AgentReleases>();
builder.Services.AddSingleton<RollupRefresher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RollupRefresher>());
builder.Services.AddHostedService<BatchProcessor>();
builder.Services.AddHttpClient("telegram", c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHttpClient("telegram-poll", c => c.Timeout = TimeSpan.FromSeconds(options.TelegramPollSeconds + 20));
builder.Services.AddSingleton<TelegramApi>();
builder.Services.AddSingleton<TelegramBot>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TelegramBot>());
builder.Services.AddSingleton<AnomalyDetector>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AnomalyDetector>());
builder.Services.AddHostedService<StationMonitor>();
builder.Services.AddUiAuth(options.SecureCookies);
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("topaz-center");
if (!string.IsNullOrWhiteSpace(options.KeysDirectory))
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(options.KeysDirectory));
Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = Protocol.Json.PropertyNamingPolicy;
    o.SerializerOptions.DictionaryKeyPolicy = null;
    // UI clients compare with null: always write null properties.
    o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never;
});
builder.Services.AddProblemDetails();
builder.Services.AddRequestDecompression(); // agents send gzip; decompressed size is capped by MaxRequestBodySize
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = options.MaxRequestBodyBytes);

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // Only the reverse proxy (Caddy) can reach the app port inside the Docker network.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = (ctx, _) =>
    {
        ctx.HttpContext.Response.Headers.RetryAfter = "30";
        return ValueTask.CompletedTask;
    };
    // Enrollment: brute-force protection for the install key.
    // Connection requests are unauthenticated: few per address; polling once a minute is plenty.
    o.AddPolicy("registration", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = options.RegistrationsPerHourPerIp, Window = TimeSpan.FromHours(1) }));
    o.AddPolicy("registration-claim", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(10) }));
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = options.LoginsPerMinutePerIp, Window = TimeSpan.FromMinutes(1) }));
    // Web interface: per signed-in user (anonymous: per address); login has its own stricter policy.
    o.AddPolicy("ui", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = options.UiRequestsPerMinute, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("station", ctx => RateLimitPartition.GetFixedWindowLimiter(
        Tokens.Bearer(ctx) is { } t ? Convert.ToHexString(Tokens.Hash(t)) : ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = options.BatchesPerMinutePerStation, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);

var app = builder.Build();

if (string.IsNullOrEmpty(options.AdminToken) || options.AdminToken.Length < 24)
    app.Logger.LogWarning("Topaz__AdminToken is not set or shorter than 24 chars: admin and export API are unusable");
if (!string.IsNullOrEmpty(options.EnrollmentKey))
    app.Logger.LogWarning("Topaz__EnrollmentKey is set but no longer used: stations connect by request (АЗС → Подключение). Remove it from .env");

await app.Services.GetRequiredService<Migrator>().MigrateAsync(CancellationToken.None);
await app.Services.GetRequiredService<Topaz.Central.Updates.AgentReleases>().ScanAsync(CancellationToken.None);

if (args.FirstOrDefault() == "create-user")
    return await UserCommands.CreateUserAsync(app.Services, args[1..]);
if (args.FirstOrDefault() == "reset-totp")
    return await UserCommands.ResetTotpAsync(app.Services, args[1..]);
await UserCommands.BootstrapOwnerAsync(app.Services, options, app.Logger);

app.UseForwardedHeaders();
app.UseExceptionHandler(e => e.Run(async ctx =>
{
    var error = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    if (error is BadHttpRequestException bad)
    {
        // Client errors (e.g. 413 body too large, broken gzip) keep their status: the agent must not retry them.
        await Results.Problem(statusCode: bad.StatusCode, title: bad.Message).ExecuteAsync(ctx);
        return;
    }
    // Unexpected errors (e.g. database down) are temporary for the agent: 503 + Retry-After → retried.
    ctx.Response.Headers.RetryAfter = "30";
    await Results.Problem(statusCode: 503, title: "Temporarily unavailable").ExecuteAsync(ctx);
}));
app.UseSerilogRequestLogging(o => o.GetLevel = (ctx, _, ex) =>
    ex is not null || ctx.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error
    : ctx.Request.Path.StartsWithSegments("/health") || ctx.Request.Path.StartsWithSegments("/metrics") ? Serilog.Events.LogEventLevel.Verbose
    : Serilog.Events.LogEventLevel.Information);
app.UseRequestDecompression();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = f => AppVersion.SetCacheHeaders(f.Context, f.File.Name) });
// The web UI compares this with its own build version and offers a reload after an update.
app.Use((ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments(Protocol.ApiPrefix + "/ui"))
        ctx.Response.OnStarting(() => { ctx.Response.Headers[AppVersion.Header] = AppVersion.Current; return Task.CompletedTask; });
    return next();
});
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseHttpMetrics();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
app.MapMetrics("/metrics");
app.MapIngest();
app.MapAdmin();

var ui = app.MapGroup(Protocol.ApiPrefix + "/ui").RequireAuthorization(Policies.Ui).RequireRateLimiting("ui")
    .AddEndpointFilter<UiAuthSetup.CsrfFilter>().AddEndpointFilter<UiAuthSetup.TotpRequiredFilter>();
ui.MapUiAuth();
ui.MapUiOverview();
ui.MapUiShifts();
ui.MapUiData();
ui.MapUiAdmin();
ui.MapUiSalesAnalytics();
ui.MapUiFuelCatalog();
ui.MapUiMoney();
ui.MapUiControl();
ui.MapUiAgentUpdates();
ui.MapUiRegistrations();
ui.MapUiOrganizations();
// SPA client-side routes: GET only and never under /api. A 200 for an unknown API path would make an
// agent with a misconfigured URL treat batches as delivered and drop them.
app.MapFallback(async ctx =>
{
    var index = app.Environment.WebRootFileProvider.GetFileInfo("index.html");
    // A missing file (an old tab asking for /assets/… of the previous release) gets 404, not the page as "script".
    if (!HttpMethods.IsGet(ctx.Request.Method) || ctx.Request.Path.StartsWithSegments("/api") || AppVersion.LooksLikeFile(ctx.Request.Path) || !index.Exists)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    ctx.Response.ContentType = "text/html; charset=utf-8";
    ctx.Response.Headers.CacheControl = "no-cache";
    await ctx.Response.SendFileAsync(index);
}).AllowAnonymous();

app.Run();
return 0;

internal sealed class PostgresHealthCheck(NpgsqlDataSource db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var cmd = db.CreateCommand("SELECT 1");
            await cmd.ExecuteScalarAsync(ct);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL unreachable", ex);
        }
    }
}

public partial class Program;
