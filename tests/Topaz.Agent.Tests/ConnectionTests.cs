using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Topaz.Agent.Configuration;
using Topaz.Agent.Hosting;
using Topaz.Agent.Sending;
using Topaz.Agent.State;
using Topaz.Contracts;
using Xunit;

namespace Topaz.Agent.Tests;

/// <summary>Connection by request: code, polling, approval, expiry, rejection; the secret never travels in the request.</summary>
public sealed class ConnectionTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly StateStore _state;
    private readonly TokenStore _tokens;
    private readonly FakeHttp _http = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly Connection _connection;
    private readonly List<RegistrationRequest> _requests = new();
    private readonly List<RegistrationClaimRequest> _claims = new();

    public ConnectionTests()
    {
        _state = new StateStore(_dir.Path);
        _tokens = new TokenStore(_state);
        var options = new AgentOptions { Server = { Url = "https://central.test" }, Station = { Name = "АЗС-5 Северная" } };
        var topaz = new FakeTopaz();
        _connection = new Connection(_state, new CentralClient(new HttpClient(_http), options), _tokens, options, new AgentStatus(), topaz,
            new DatasetCatalog(), _time, NullLogger<Connection>.Instance);
    }

    public void Dispose()
    {
        _state.Dispose();
        _dir.Dispose();
    }

    private static HttpResponseMessage Json<T>(HttpStatusCode code, T body) => new(code) { Content = JsonContent.Create(body, options: Protocol.Json) };

    /// <summary>A server whose claim answer is decided by <paramref name="claim"/>.</summary>
    private void Server(Func<int, HttpResponseMessage> claim)
    {
        _http.Responder = req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v1/registrations")
            {
                var r = req.Content!.ReadFromJsonAsync<RegistrationRequest>(Protocol.Json).GetAwaiter().GetResult()!;
                _requests.Add(r);
                return Json(HttpStatusCode.Created, new RegistrationResponse { RegistrationId = Guid.NewGuid(), VerificationCode = RegistrationCode.Verification(r.ClaimHash) });
            }
            _claims.Add(req.Content!.ReadFromJsonAsync<RegistrationClaimRequest>(Protocol.Json).GetAwaiter().GetResult()!);
            return claim(_claims.Count);
        };
    }

    [Fact]
    public async Task Waits_with_a_code_then_saves_the_token_once_approved()
    {
        var stationId = Guid.NewGuid();
        Server(n => n < 3
            ? Json(HttpStatusCode.Accepted, new RegistrationClaimResponse { Status = "pending", RetryAfterSeconds = 60 })
            : Json(HttpStatusCode.OK, new RegistrationClaimResponse { Status = "approved", StationId = stationId, StationName = "АЗС-5", Token = "tpz_ok" }));

        var (token, view, wait) = await _connection.StepAsync(default);
        Assert.Null(token);
        Assert.Equal(ConnectionState.Pending, view.State);
        Assert.Equal(TimeSpan.FromSeconds(60), wait);
        var request = Assert.Single(_requests);
        Assert.Equal("АЗС-5 Северная", request.StationName);
        Assert.Equal(RegistrationCode.Verification(request.ClaimHash), view.Code);
        // The secret only goes with the claim, and its hash is what the request carried.
        Assert.Equal(request.ClaimHash, RegistrationCode.ClaimHash(_claims[0].Secret));
        Assert.StartsWith(OperatingSystem.IsWindows() ? "dpapi:" : "plain:", _state.GetKv("registration_secret"));

        Assert.Null((await _connection.StepAsync(default)).Token);   // too early: no network call
        Assert.Single(_claims);
        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.Null((await _connection.StepAsync(default)).Token);
        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("tpz_ok", (await _connection.StepAsync(default)).Token);
        Assert.Equal("tpz_ok", _tokens.GetToken());
        Assert.Equal(stationId.ToString(), _tokens.StationId);
        Assert.Null(_state.GetKv("registration_secret"));               // forgotten once connected
        Assert.Single(_requests);                                      // one request all along
    }

    [Fact]
    public async Task Expired_request_is_replaced_and_rejection_pauses_for_a_day()
    {
        Server(n => n == 1 ? new HttpResponseMessage(HttpStatusCode.Gone)
            : Json(HttpStatusCode.Forbidden, new RegistrationClaimResponse { Status = "rejected", RetryAfterSeconds = 86400 }));

        var first = await _connection.StepAsync(default);   // request + 410
        Assert.Equal(TimeSpan.Zero, first.Wait);
        var second = await _connection.StepAsync(default);  // new request (new code) + rejected
        Assert.Equal(2, _requests.Count);
        Assert.NotEqual(_requests[0].ClaimHash, _requests[1].ClaimHash);
        Assert.Equal(ConnectionState.Rejected, second.View.State);
        Assert.Equal(TimeSpan.FromHours(24), second.Wait);
        _time.Advance(TimeSpan.FromHours(23));
        await _connection.StepAsync(default);
        Assert.Equal(2, _requests.Count);                    // still waiting
    }

    [Fact]
    public async Task Network_problems_back_off_without_new_requests()
    {
        _http.Responder = _ => FakeHttp.Throw();
        var (_, view, wait) = await _connection.StepAsync(default);
        Assert.Equal(ConnectionState.Offline, view.State);
        Assert.True(wait > TimeSpan.Zero);
        Assert.Null(_state.GetKv("registration_id"));
    }
}
