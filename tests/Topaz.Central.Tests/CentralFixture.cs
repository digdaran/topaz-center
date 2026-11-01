using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Topaz.Contracts;
using Xunit;

namespace Topaz.Central.Tests;

/// <summary>Runs only when PostgreSQL is available: TOPAZ_TEST_PG = admin connection string.</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TOPAZ_TEST_PG")))
            Skip = "Set TOPAZ_TEST_PG (e.g. Host=localhost;Username=postgres;Password=...) to run central tests";
    }
}

/// <summary>Real ASP.NET pipeline + a fresh PostgreSQL database per test class.</summary>
public sealed class CentralFixture : IAsyncLifetime
{
    public const string EnrollKey = "test-enroll-key";
    public const string AdminToken = "test-admin-token-0123456789abcdef";

    private string? _adminCs;
    private string _dbName = "";
    public string ConnectionString { get; private set; } = "";
    public WebApplicationFactory<Program>? Factory { get; private set; }

    public async Task InitializeAsync()
    {
        _adminCs = Environment.GetEnvironmentVariable("TOPAZ_TEST_PG");
        if (string.IsNullOrEmpty(_adminCs)) return;
        _dbName = "topaz_test_" + Guid.NewGuid().ToString("N")[..12];
        await using (var c = new NpgsqlConnection(_adminCs))
            await c.ExecuteAsync($"CREATE DATABASE {_dbName}");
        ConnectionString = new NpgsqlConnectionStringBuilder(_adminCs) { Database = _dbName }.ToString();

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", ConnectionString);
            b.UseSetting("Topaz:RegistrationsPerHourPerIp", "100000");
            b.UseSetting("Topaz:AdminToken", AdminToken);
            b.UseSetting("Topaz:MaxProcessingAttempts", "2");
        });
        _ = Factory.Server; // start (runs migrations)
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null) await Factory.DisposeAsync();
        if (string.IsNullOrEmpty(_adminCs)) return;
        NpgsqlConnection.ClearAllPools();
        await using var c = new NpgsqlConnection(_adminCs);
        await c.ExecuteAsync($"DROP DATABASE IF EXISTS {_dbName} WITH (FORCE)");
    }

    public HttpClient Client(string? bearer = null)
    {
        var client = Factory!.CreateClient();
        if (bearer is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client;
    }

    /// <summary>Connects a station the way production does: request → superadmin approval with the code → claim.</summary>
    public async Task<EnrollResponse> EnrollAsync(string name)
    {
        var secret = RegistrationCode.NewSecret();
        var created = await Client().PostAsJsonAsync("/api/v1/registrations",
            new RegistrationRequest { StationName = name, ClaimHash = RegistrationCode.ClaimHash(secret), MachineName = "TEST-PC", AgentVersion = "t" }, Protocol.Json);
        created.EnsureSuccessStatusCode();
        var reg = (await created.Content.ReadFromJsonAsync<RegistrationResponse>(Protocol.Json))!;
        var (result, _) = await Factory!.Services.GetRequiredService<Topaz.Central.Auth.Registrations>()
            .ApproveAsync(reg.RegistrationId, reg.VerificationCode, null, null, "test");
        if (result != Topaz.Central.Auth.Registrations.Decision.Ok) throw new InvalidOperationException("approval failed: " + result);
        var claim = await Client().PostAsJsonAsync("/api/v1/registrations/claim", new RegistrationClaimRequest { RegistrationId = reg.RegistrationId, Secret = secret }, Protocol.Json);
        claim.EnsureSuccessStatusCode();
        var c = (await claim.Content.ReadFromJsonAsync<RegistrationClaimResponse>(Protocol.Json))!;
        return new EnrollResponse { StationId = c.StationId!.Value, StationName = c.StationName!, Token = c.Token! };
    }

    public async Task<T> QueryAsync<T>(string sql, object? args = null)
    {
        await using var c = new NpgsqlConnection(ConnectionString);
        return await c.QuerySingleAsync<T>(sql, args);
    }

    /// <summary>Waits until every received batch is processed and the daily rollups are refreshed.</summary>
    public async Task WaitProcessedAsync()
    {
        for (var i = 0; i < 200; i++)
        {
            if (await QueryAsync<long>("SELECT (SELECT count(*) FROM raw_batches WHERE status = 'received') + (SELECT count(*) FROM rollup_dirty)") == 0) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("Batches were not processed in time");
    }
}
