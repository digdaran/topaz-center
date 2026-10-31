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
builder.Services.AddSingleton(_ => new NpgsqlDataSourceBuilder(connectionString).Build());

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<StationAuthenticator>();
builder.Services.AddSingleton<StationAuthFilter>();
builder.Services.AddSingleton<AdminAuthFilter>();
builder.Services.AddSingleton<BatchSignal>();
builder.Services.AddSingleton<Migrator>();
builder.Services.AddHostedService<BatchProcessor>();
builder.Services.AddHostedService<StationMonitor>();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = Protocol.Json.PropertyNamingPolicy;
    o.SerializerOptions.DictionaryKeyPolicy = null;
    o.SerializerOptions.DefaultIgnoreCondition = Protocol.Json.DefaultIgnoreCondition;
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
    o.AddPolicy("enroll", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("station", ctx => RateLimitPartition.GetFixedWindowLimiter(
        Tokens.Bearer(ctx) is { } t ? Convert.ToHexString(Tokens.Hash(t)) : ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = options.BatchesPerMinutePerStation, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);

var app = builder.Build();

if (string.IsNullOrEmpty(options.AdminToken) || options.AdminToken.Length < 24)
    app.Logger.LogWarning("Topaz__AdminToken is not set or shorter than 24 chars: admin and export API are unusable");
if (string.IsNullOrEmpty(options.EnrollmentKey))
    app.Logger.LogWarning("Topaz__EnrollmentKey is not set: new agents cannot enroll");

await app.Services.GetRequiredService<Migrator>().MigrateAsync(CancellationToken.None);

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
app.UseRateLimiter();
app.UseHttpMetrics();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
app.MapMetrics("/metrics");
app.MapIngest();
app.MapAdmin();

app.Run();

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
