using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using TradingBot.Arena;
using Xunit;
namespace TradingBot.Arena.Tests;

public sealed class ArenaSubmissionTests
{
    private const string Key = "fixture-arena-key";
    private static readonly Uri Origin = new("http://127.0.0.1:49153");
    private static readonly ArenaRegistryListing[] Registry = [new("PF_XBTUSD", null, null, 1)];
    private sealed class Fake(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        public readonly List<(string Method, string Path, string Body)> Requests = [];
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.True(request.RequestUri!.IsLoopback); Assert.Equal(Key, request.Headers.Authorization!.Parameter);
            Assert.DoesNotContain(Key, request.RequestUri.ToString());
            Requests.Add((request.Method.Method, request.RequestUri.AbsolutePath, await request.Content!.ReadAsStringAsync(ct)));
            return _responses.Dequeue();
        }
    }
    private static HttpResponseMessage Response(int status, string body = "{\"accepted\":1,\"updated\":0}") => new((HttpStatusCode)status) { Content = new StringContent(body) };
    private static ArenaContract Contract() => new(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/arena-openapi.json")));
    private static ArenaImportPlan Plan(ArenaContract contract)
    {
        var p = new JsonObject { ["handle"] = "omenas", ["declared_capital"] = "100", ["capital_ccy"] = "USD", ["effective_from"] = "2026-10-11T00:00:00Z" };
        var t = JsonNode.Parse("{\"trade_id\":\"kraken-PF_XBTUSD-first\",\"venue\":\"KRAKEN-FUTURES\",\"instrument\":\"PF_XBTUSD\",\"side\":\"long\",\"account\":\"live\",\"opened_at\":\"2026-10-11T00:00:00Z\",\"closed_at\":\"2026-10-11T01:00:00Z\",\"entry_price\":\"100\",\"exit_price\":\"110\",\"size\":\"1\",\"size_unit\":\"base\",\"leverage\":\"10\",\"fees\":\".1\",\"funding\":\"0\",\"label\":\"BYKO\"}")!.AsObject();
        return new(p, [t], new() { ["account"] = "live", ["positions"] = new JsonArray() }, "source-hash", contract.SchemaHash, ArenaSubmission.RegistryHash(Registry));
    }
    private static Task<DateTimeOffset> Current(JsonObject _, CancellationToken ct) => Task.FromResult(DateTimeOffset.UtcNow);
    private static Task Checkpoint(ArenaReceipt _, CancellationToken ct) => Task.CompletedTask;
    [Fact]
    public async Task Full_preflight_approval_and_contract_checks_precede_every_write()
    {
        var fake = new Fake(); var client = new ArenaSubmission(new HttpClient(fake), Key, Origin); var contract = Contract(); var plan = Plan(contract);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ImportAsync(plan, "wrong-digest", contract, Registry, Current, Checkpoint, default));
        plan.Trades[0]["size"] = "-1";
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ImportAsync(plan, plan.Digest, contract, Registry, Current, Checkpoint, default));
        Assert.Empty(fake.Requests);
    }
    [Fact]
    public async Task Profile_trades_positions_are_ordered_and_receipts_are_checkpointed()
    {
        var fake = new Fake(Response(202), Response(202), Response(202, "{\"accepted\":0,\"updated\":0}")); var contract = Contract(); var plan = Plan(contract); List<ArenaReceipt> checkpoints = [];
        var client = new ArenaSubmission(new HttpClient(fake), Key, Origin);
        var receipts = await client.ImportAsync(plan, plan.Digest, contract, Registry, Current, (r, _) => { checkpoints.Add(r); return Task.CompletedTask; }, default);
        Assert.Equal(new[] { "/v1/profile", "/v1/trades", "/v1/positions" }, fake.Requests.Select(r => r.Path));
        Assert.Equal("PUT", fake.Requests[0].Method); Assert.Equal(receipts, checkpoints);
    }
    [Fact]
    public async Task A_recorded_profile_is_not_redeclared_when_resuming_after_a_trade_timeout()
    {
        var fake = new Fake(Response(200, "{\"accepted\":0,\"updated\":0}"), Response(202, "{\"accepted\":0,\"updated\":0}"));
        var contract = Contract(); var plan = Plan(contract); var client = new ArenaSubmission(new HttpClient(fake), Key, Origin);
        await client.ImportAsync(plan, plan.Digest, contract, Registry, Current, Checkpoint, default, profileAlreadyRecorded: true);
        Assert.Equal(new[] { "/v1/trades", "/v1/positions" }, fake.Requests.Select(r => r.Path));
    }
    [Fact]
    public async Task History_is_submitted_one_position_per_request_without_trimming_the_plan()
    {
        var fake = new Fake(Response(202), Response(202), Response(202, "{\"accepted\":0,\"updated\":0}"));
        var contract = Contract(); var original = Plan(contract); var second = original.Trades[0].DeepClone().AsObject(); second["trade_id"] = "kraken-PF_XBTUSD-second";
        var plan = original with { Trades = [original.Trades[0], second] }; var client = new ArenaSubmission(new HttpClient(fake), Key, Origin);
        var receipts = await client.ImportAsync(plan, plan.Digest, contract, Registry, Current, Checkpoint, default, profileAlreadyRecorded: true);
        Assert.Equal(2, receipts.Where(r => r.Path == "/v1/trades").Sum(r => r.Accepted));
        Assert.All(fake.Requests.Where(r => r.Path == "/v1/trades"), r => Assert.Single(JsonNode.Parse(r.Body)!.AsArray()));
    }
    [Fact]
    public async Task A_400_is_not_retried_and_known_key_is_redacted()
    {
        var fake = new Fake(Response(400, Key + " invalid")); var contract = Contract(); var plan = Plan(contract);
        var client = new ArenaSubmission(new HttpClient(fake), Key, Origin);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ImportAsync(plan, plan.Digest, contract, Registry, Current, Checkpoint, default));
        Assert.Single(fake.Requests); Assert.Contains("400", error.Message); Assert.DoesNotContain(Key, error.Message);
    }
    [Fact]
    public async Task Retry_after_is_honoured_and_identical_resend_may_be_a_noop()
    {
        var limited = Response(429); limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        var fake = new Fake(limited, Response(202), Response(200, "{\"accepted\":0,\"updated\":0}"), Response(202, "{\"accepted\":0,\"updated\":0}"));
        var contract = Contract(); var plan = Plan(contract); List<TimeSpan> waits = [];
        var client = new ArenaSubmission(new HttpClient(fake), Key, Origin, delay: (wait, _) => { waits.Add(wait); return Task.CompletedTask; });
        var receipts = await client.ImportAsync(plan, plan.Digest, contract, Registry, Current, Checkpoint, default);
        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(waits)); Assert.Equal(fake.Requests[0], fake.Requests[1]);
        Assert.Equal(0, receipts[1].Accepted); Assert.Equal(0, receipts[1].Updated);
    }
    [Fact]
    public async Task Stale_snapshot_blocks_profile_and_failed_checkpoint_blocks_next_batch()
    {
        var fake = new Fake(Response(202)); var contract = Contract(); var plan = Plan(contract); var client = new ArenaSubmission(new HttpClient(fake), Key, Origin);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ImportAsync(plan, plan.Digest, contract, Registry, (_, _) => Task.FromResult(DateTimeOffset.UtcNow.AddMinutes(-2)), Checkpoint, default));
        Assert.Empty(fake.Requests);
        await Assert.ThrowsAsync<IOException>(() => client.ImportAsync(plan, plan.Digest, contract, Registry, Current, (_, _) => throw new IOException("disk full"), default));
        Assert.Single(fake.Requests);
    }
    [Fact]
    public void Fresh_native_positions_must_match_approved_bot_size_direction_and_entry()
    {
        var expected = JsonNode.Parse("{\"positions\":[{\"instrument\":\"PF_XBTUSD\",\"side\":\"short\",\"size\":\"2\",\"entry_price\":\"100\"}]}")!.AsObject();
        var native = JsonNode.Parse("{\"openPositions\":[{\"symbol\":\"PF_XBTUSD\",\"side\":\"short\",\"size\":2,\"price\":100}]}")!.AsObject();
        ArenaSnapshotCheck.Matches(native, expected);
        native["openPositions"]![0]!["size"] = 3;
        Assert.Throws<InvalidDataException>(() => ArenaSnapshotCheck.Matches(native, expected));
        Assert.Throws<InvalidDataException>(() => ArenaSnapshotCheck.Matches(new(), expected));
    }
}
