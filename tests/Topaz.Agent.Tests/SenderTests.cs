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

/// <summary>Bad-connectivity scenarios: offline, flapping, rejected, revoked token.</summary>
public sealed class SenderTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly StateStore _state;
    private readonly FakeHttp _http = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    private readonly AgentOptions _options = new()
    {
        Server = new ServerOptions { Url = "https://central.test", EnrollmentKey = "k", RetryBaseSeconds = 5, RetryMaxSeconds = 900 },
        Station = new StationOptions { Name = "АЗС-1" },
    };
    private readonly Sender _sender;
    private readonly TokenStore _tokens;

    public SenderTests()
    {
        _state = new StateStore(_dir.Path);
        _tokens = new TokenStore(_state);
        _tokens.Save(Guid.NewGuid(), "tpz_token");
        _sender = new Sender(_state, new CentralClient(new HttpClient(_http), _options), _tokens, _options, new AgentStatus(), _time,
            NullLogger<Sender>.Instance);
    }

    private void Queue(params string[] keys)
    {
        var commit = new DatasetCommit { Dataset = "dcFuels" };
        foreach (var k in keys)
        {
            var row = FakeTopaz.Row(k, null, new { FuelID = k });
            commit.Changes.Add(new RecordChange { Dataset = "dcFuels", Key = k, Hash = row.Hash, Data = row.Data });
            commit.StateUpserts[k] = new RowStateEntry(row.Hash, null);
        }
        _state.Commit(commit, 1, "test", _time.GetUtcNow());
    }

    [Fact]
    public async Task Offline_keeps_batch_and_retries_with_growing_backoff_then_delivers_once()
    {
        Queue("1");
        _http.Responder = _ => FakeHttp.Throw();

        var delays = new List<TimeSpan>();
        for (var i = 0; i < 6; i++)
        {
            var (step, delay) = await _sender.StepAsync(default);
            Assert.Equal(SendStep.Failed, step);
            delays.Add(delay);
            _time.Advance(delay);
        }
        Assert.Equal(1, _state.GetQueueStats().Pending);
        Assert.All(delays, d => Assert.InRange(d.TotalSeconds, 1, 900));

        _http.Responder = _ => new HttpResponseMessage(HttpStatusCode.Accepted);
        var (ok, _) = await _sender.StepAsync(default);
        Assert.Equal(SendStep.Sent, ok);
        Assert.Equal(0, _state.GetQueueStats().Pending);
        // Every attempt carried the same idempotency key: the server stores the batch once.
        Assert.Single(_http.Requests.Select(r => r.IdempotencyKey).Distinct());
    }

    [Fact]
    public async Task Not_yet_due_batch_is_not_sent()
    {
        Queue("1");
        _http.Responder = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var (_, delay) = await _sender.StepAsync(default);
        var (step, _) = await _sender.StepAsync(default); // no time passed
        Assert.Equal(SendStep.Waiting, step);
        Assert.Single(_http.Requests);
        Assert.True(delay > TimeSpan.Zero);
    }

    [Fact]
    public async Task Retry_after_header_is_honoured()
    {
        Queue("1");
        _http.Responder = _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
            return r;
        };
        var (_, delay) = await _sender.StepAsync(default);
        Assert.Equal(42, delay.TotalSeconds);
    }

    [Fact]
    public async Task Batches_are_sent_in_order_and_head_of_line_blocks()
    {
        Queue("1", "2", "3"); // 3 batches of 1 item
        var sentKeys = new List<string>();
        var failSecond = true;
        _http.Responder = req =>
        {
            var body = StateStore.Gunzip(req.Content!.ReadAsByteArrayAsync().Result);
            var key = body.Items.Single().Key;
            if (key == "2" && failSecond) { failSecond = false; return new HttpResponseMessage(HttpStatusCode.BadGateway); }
            sentKeys.Add(key);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        };
        for (var i = 0; i < 10 && _state.GetQueueStats().Pending > 0; i++)
        {
            var (_, delay) = await _sender.StepAsync(default);
            _time.Advance(delay);
        }
        Assert.Equal(["1", "2", "3"], sentKeys);
    }

    [Fact]
    public async Task Rejected_batch_goes_to_dead_letters_and_its_rows_will_be_reread()
    {
        Queue("1");
        _http.Responder = _ => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity) { Content = new StringContent("bad") };
        await _sender.StepAsync(default);

        var stats = _state.GetQueueStats();
        Assert.Equal(0, stats.Pending);
        Assert.Equal(1, stats.Dead);
        Assert.Empty(_state.LoadRowState("dcFuels")); // forgotten → next cycle re-sends it

        Assert.Equal(1, _state.RequeueDead(_time.GetUtcNow()));
        Assert.Equal(1, _state.GetQueueStats().Pending);
    }

    [Fact]
    public async Task Revoked_token_triggers_reenrollment_and_queue_is_kept()
    {
        Queue("1");
        _http.Responder = req => req.RequestUri!.AbsolutePath switch
        {
            "/api/v1/enroll" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new EnrollResponse { StationId = Guid.NewGuid(), StationName = "АЗС-1", Token = "tpz_new" }, options: Protocol.Json),
            },
            _ when req.Headers.Authorization!.Parameter == "tpz_token" => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            _ => new HttpResponseMessage(HttpStatusCode.Accepted),
        };

        await _sender.StepAsync(default); // 401 → token cleared
        Assert.Null(_tokens.GetToken());
        await _sender.StepAsync(default); // enroll + send
        Assert.Equal("tpz_new", _tokens.GetToken());
        Assert.Equal(0, _state.GetQueueStats().Pending);
    }

    [Fact]
    public void Backoff_is_bounded()
    {
        var rnd = new Random(1);
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var d = Backoff.Next(attempt, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(900), rnd);
            Assert.InRange(d.TotalSeconds, 1, 900);
        }
    }

    public void Dispose()
    {
        _state.Dispose();
        _dir.Dispose();
    }
}
