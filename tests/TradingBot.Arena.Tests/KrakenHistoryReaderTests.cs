using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using TradingBot.Arena;
using Xunit;

namespace TradingBot.Arena.Tests;

public sealed class KrakenHistoryReaderTests
{
    private const string Account = "11111111-1111-1111-1111-111111111111";
    private const string Key = "fixture-private-key";
    private static readonly string Secret = Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture-private-secret"));
    private static HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(text) };
    private static string Identity(string general = "READ_ONLY") => new JsonObject { ["accountUid"] = Account, ["apiKey"] = Key,
        ["permissions"] = new JsonObject { ["general"] = general, ["transfer"] = "NO_ACCESS" } }.ToJsonString();
    private static string Events(string rows, string? token = null) => new JsonObject { ["accountUid"] = Account,
        ["elements"] = JsonNode.Parse("[" + rows + "]"), ["continuationToken"] = token }.ToJsonString();
    private static string Ledger(string rows) => new JsonObject { ["accountUid"] = Account, ["logs"] = JsonNode.Parse("[" + rows + "]") }.ToJsonString();
    private static string Open => "{\"result\":\"success\",\"openPositions\":[]}";
    private sealed class Fake(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, Uri Uri, string Key, string Signature)> Requests = [];
        public readonly List<bool> HasNonce = [];
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            HasNonce.Add(request.Headers.Contains("Nonce"));
            Requests.Add((request.Method, request.RequestUri!, request.Headers.GetValues("APIKey").Single(), request.Headers.GetValues("Authent").Single()));
            Assert.True(request.RequestUri!.IsLoopback);
            return Task.FromResult(_responses.Dequeue());
        }
    }
    private static KrakenHistoryReader Reader(Fake fake, List<TimeSpan>? delays = null) => new(new HttpClient(fake), Key, Secret,
        new Uri("http://127.0.0.1:49152"), delay: (duration, ct) => { delays?.Add(duration); return Task.CompletedTask; });
    [Fact]
    public async Task Reads_all_execution_tokens_and_inclusive_ledger_ID_pages_even_after_short_pages()
    {
        var fake = new Fake(Json(Identity()), Json(Events("{\"uid\":\"event-1\"}", "token-a")), Json(Events("{\"uid\":\"event-2\"}")),
            Json(Events("")), Json(Ledger("{\"id\":3}")), Json(Ledger("{\"id\":1}")), Json(Open));
        HistoryCapture capture = await Reader(fake).CaptureAsync(default);
        Assert.True(capture.Complete); Assert.Equal(2, capture.Executions.Count); Assert.Equal(2, capture.Ledger.Count);
        Assert.Equal(Account, capture.AccountUid);
        Assert.Contains("continuation_token=token-a", fake.Requests[2].Uri.Query);
        Assert.Contains("to=2", fake.Requests[5].Uri.Query);
        Assert.All(fake.Requests, r => { Assert.Equal(HttpMethod.Get, r.Method); Assert.Equal(Key, r.Key); Assert.NotEmpty(r.Signature); Assert.DoesNotContain(Key, r.Uri.ToString()); });
        Assert.DoesNotContain(Key, System.Text.Json.JsonSerializer.Serialize(capture));
    }
    [Fact]
    public async Task Explicit_existing_key_mode_is_GET_only_and_does_not_advance_working_key_nonce()
    {
        var fake = new Fake(Json(Identity("FULL_ACCESS")), Json(Events("")), Json(Events("")), Json(Ledger("")), Json(Open));
        var reader = new KrakenHistoryReader(new HttpClient(fake), Key, Secret, new Uri("http://127.0.0.1:49152"),
            delay: (_, _) => Task.CompletedTask, existingKeyReadOnly: true);
        var capture = await reader.CaptureAsync(default);
        Assert.True(capture.Complete);
        Assert.All(fake.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.All(fake.HasNonce, n => Assert.False(n));
    }
    [Fact]
    public async Task Position_scope_UID_is_not_a_unique_event_ID_and_every_token_is_read()
    {
        const string a = "{\"uid\":\"same-scope\",\"timestamp\":1,\"event\":{\"PositionUpdate\":{\"executionUid\":\"fill-a\"}}}";
        const string b = "{\"uid\":\"same-scope\",\"timestamp\":2,\"event\":{\"PositionUpdate\":{\"executionUid\":\"fill-b\"}}}";
        var fake = new Fake(Json(Identity()), Json(Events("")), Json(Events(a, "position-token")), Json(Events(b)), Json(Ledger("")), Json(Open));
        var capture = await Reader(fake).CaptureAsync(default);
        Assert.Equal(2, capture.PositionUpdates!.Count);
        Assert.Contains("continuation_token=position-token", fake.Requests[3].Uri.Query);
    }
    [Fact]
    public async Task Fresh_snapshot_rechecks_key_identity_and_never_reads_history_or_trades()
    {
        var fake = new Fake(Json(Identity("FULL_ACCESS")), Json(Open));
        var reader = new KrakenHistoryReader(new HttpClient(fake), Key, Secret, new Uri("http://127.0.0.1:49152"), existingKeyReadOnly: true);
        var snapshot = await reader.CurrentOpenPositionsAsync(default);
        Assert.Equal(Account, snapshot.AccountUid); Assert.Equal(2, fake.Requests.Count);
        Assert.All(fake.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.All(fake.HasNonce, n => Assert.False(n));
    }
    [Theory]
    [InlineData("FULL_ACCESS")]
    [InlineData("NO_ACCESS")]
    public async Task Refuses_the_trading_or_unknown_permission_key_before_history(string permission)
    {
        var fake = new Fake(Json(Identity(permission)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Reader(fake).CaptureAsync(default));
        Assert.Single(fake.Requests);
    }
    [Fact]
    public async Task Repeated_continuation_token_never_produces_a_complete_capture()
    {
        var fake = new Fake(Json(Identity()), Json(Events("{\"uid\":\"one\"}", "stuck")), Json(Events("{\"uid\":\"two\"}", "stuck")));
        await Assert.ThrowsAsync<InvalidDataException>(() => Reader(fake).CaptureAsync(default));
    }
    [Fact]
    public async Task Missing_array_is_an_error_not_an_empty_history()
    {
        var fake = new Fake(Json(Identity()), Json($$"""{"accountUid":"{{Account}}"}"""));
        await Assert.ThrowsAsync<InvalidDataException>(() => Reader(fake).CaptureAsync(default));
    }
    [Fact]
    public async Task Another_account_on_a_later_page_is_refused()
    {
        var fake = new Fake(Json(Identity()), Json(Events("").Replace(Account, "other-account")));
        await Assert.ThrowsAsync<InvalidDataException>(() => Reader(fake).CaptureAsync(default));
    }
    [Fact]
    public async Task Retry_after_is_honoured_without_real_time_waits()
    {
        var limited = Json("limited", HttpStatusCode.TooManyRequests); limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        var fake = new Fake(Json(Identity()), limited, Json(Events("")), Json(Events("")), Json(Ledger("")), Json(Open));
        List<TimeSpan> delays = [];
        var capture = await Reader(fake, delays).CaptureAsync(default);
        Assert.True(capture.Complete); Assert.Contains(TimeSpan.FromSeconds(30), delays);
    }
    [Fact]
    public async Task Permanent_errors_are_not_retried_and_credentials_are_redacted()
    {
        var fake = new Fake(Json(Identity()), Json(Key + " " + Secret, HttpStatusCode.Forbidden));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Reader(fake).CaptureAsync(default));
        Assert.Contains("403", ex.Message); Assert.DoesNotContain(Key, ex.Message); Assert.DoesNotContain(Secret, ex.Message); Assert.Equal(2, fake.Requests.Count);
    }
    [Fact]
    public void Private_credentials_cannot_target_an_arbitrary_service() =>
        Assert.Throws<ArgumentException>(() => new KrakenHistoryReader(new HttpClient(new Fake()), Key, Secret, new Uri("https://client.example")));
}
