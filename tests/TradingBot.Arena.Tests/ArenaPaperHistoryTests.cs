using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using TradingBot.Arena;
using Xunit;

namespace TradingBot.Arena.Tests;

public sealed class ArenaPaperHistoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 26, 9, 43, 13, TimeSpan.Zero);
    private static ArenaPaperFill Open(string side = "long") => new("cycle-open:0", "PF_XBTUSD", side, false, Start,
        5m, 100m, 100m, .25m, 10m, 50m, "MODELED_PORTFOLIO");
    private static ArenaPaperFill Close(string side = "long") => Open(side) with
    {
        Id = "cycle-close:0", Closing = true, Time = Start.AddMinutes(15), Price = 110m, Fee = .275m,
        Margin = null, ExitReason = "SELL_TAKE_PROFIT"
    };

    [Theory]
    [InlineData("long", "buy", "sell")]
    [InlineData("short", "sell", "buy")]
    public void Modeled_round_trip_is_demo_with_realized_fees_and_historical_margin(string side, string entrySide, string exitSide)
    {
        var history = ArenaPaperReconstruction.Build("futures-live", [Open(side), Close(side)], Start.AddHours(1));
        var trade = Assert.Single(history.Closed);
        Assert.Equal("demo", trade["account"]!.ToString());
        Assert.Equal("BYKO paper", trade["label"]!.ToString());
        Assert.Contains("not a real", trade["note"]!.ToString());
        Assert.Equal("0.525", trade["fees"]!.ToString());
        Assert.Equal("50", trade["margin"]!.ToString());
        Assert.Equal("10", trade["leverage"]!.ToString());
        Assert.Equal("tp", trade["exit_reason"]!.ToString());
        Assert.Equal(entrySide, trade["fills"]![0]!["side"]!.ToString());
        Assert.Equal(exitSide, trade["fills"]![1]!["side"]!.ToString());
        Assert.Null(trade["fills"]![0]!["fill_id"]); // Never invent an exchange execution ID.
        Assert.Empty(history.Open);
        Assert.StartsWith("paper-", trade["trade_id"]!.ToString());
        Assert.Equal(trade.ToJsonString(), ArenaPaperReconstruction.Build("futures-live", [Open(side), Close(side)], Start.AddHours(2)).Closed.Single().ToJsonString());
        Assert.NotEqual(trade["trade_id"]!.ToString(), ArenaPaperReconstruction.Build("another-instance", [Open(side), Close(side)], Start.AddHours(1)).Closed.Single()["trade_id"]!.ToString());
    }

    [Fact]
    public void Positions_and_flat_snapshots_are_explicitly_demo()
    {
        var history = ArenaPaperReconstruction.Build("futures-live", [Open()], Start);
        Assert.Empty(history.Closed);
        Assert.Equal("demo", history.Snapshot()["account"]!.ToString());
        Assert.Equal("demo", Assert.Single(history.Snapshot()["positions"]!.AsArray())!["account"]!.ToString());
        ArenaPaperReconstruction.CheckHoldings(history, [new("PF_XBTUSD", "long", 5m, 100m, Start, 10m)]);
        var flat = ArenaPaperReconstruction.Build("futures-live", [], Start);
        Assert.Empty(flat.Snapshot()["positions"]!.AsArray());
        Assert.Equal("demo", flat.Snapshot()["account"]!.ToString());
    }

    [Fact]
    public void Live_or_ambiguous_evidence_is_never_relabelled_as_demo()
    {
        foreach (var fill in new[] { Open() with { Source = "EXCHANGE_FILLS" }, Open() with { OrderId = "real-order" },
            Open() with { Margin = null }, Open() with { Fee = -1 }, Open() with { Leverage = 0 }, Open() with { Entry = 99 } })
            Assert.Throws<InvalidDataException>(() => ArenaPaperReconstruction.Build("futures-live", [fill], Start));
        Assert.Throws<InvalidDataException>(() => ArenaPaperReconstruction.Build("futures-live", [Close()], Start.AddHours(1)));
        Assert.Throws<InvalidDataException>(() => ArenaPaperReconstruction.Build("futures-live", [Open(), Open() with { Id = "duplicate-open" }], Start));
        foreach (var close in new[] { Close() with { Size = 4 }, Close() with { Side = "short" }, Close() with { Entry = 101 }, Close() with { Leverage = 4 } })
            Assert.Throws<InvalidDataException>(() => ArenaPaperReconstruction.Build("futures-live", [Open(), close], Start.AddHours(1)));
    }

    [Fact]
    public void Stale_or_mismatched_portfolio_cannot_be_published_as_current()
    {
        ArenaPaperReconstruction.CheckFresh(Start, Start.AddSeconds(180));
        Assert.Throws<InvalidDataException>(() => ArenaPaperReconstruction.CheckFresh(Start, Start.AddSeconds(181)));
        Assert.Throws<InvalidDataException>(() => ArenaPaperReconstruction.CheckFresh(Start.AddSeconds(1), Start));
        var history = ArenaPaperReconstruction.Build("futures-live", [Open()], Start);
        Assert.Throws<InvalidDataException>(() => ArenaPaperReconstruction.CheckHoldings(history, []));
        Assert.Throws<InvalidDataException>(() => ArenaPaperReconstruction.CheckHoldings(history, [new("PF_XBTUSD", "long", 6, 100, Start, 10)]));
        Assert.Throws<InvalidDataException>(() => ArenaPaperReconstruction.CheckHoldings(history, [new("PF_XBTUSD", "long", 5, 100, Start.AddSeconds(1), 10)]));
    }

    [Fact]
    public async Task Transport_preserves_demo_classification_without_profile_or_exchange_calls()
    {
        var handler = new LocalArena();
        using var http = new HttpClient(handler);
        var transport = new ArenaSubmission(http, "fixture", new Uri("http://127.0.0.1:49152"));
        var history = ArenaPaperReconstruction.Build("futures-live", [Open(), Close()], Start.AddHours(1));
        await transport.TradesAsync(history.Closed, default);
        await transport.PositionsAsync(history.Snapshot(), default);
        Assert.Equal(new[] { "/v1/trades", "/v1/positions" }, handler.Paths);
    }

    [Fact]
    public void Demo_payloads_match_the_recorded_Arena_API_schema_offline()
    {
        var document = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/arena-openapi.json")))!;
        var definitions = document["components"]!["schemas"]!;
        void Validate(string name, JsonNode payload)
        {
            var schema = new JsonObject { ["$defs"] = definitions.DeepClone(), ["$ref"] = "#/$defs/" + name };
            var compiled = JsonSchema.FromText(schema.ToJsonString().Replace("#/components/schemas/", "#/$defs/", StringComparison.Ordinal));
            Assert.True(compiled.Evaluate(JsonSerializer.SerializeToElement(payload), new EvaluationOptions { RequireFormatValidation = true }).IsValid);
        }
        Validate("ArenaTradeRequest", ArenaPaperReconstruction.Build("futures-live", [Open(), Close()], Start.AddHours(1)).Closed.Single());
        Validate("ArenaPositionsRequest", ArenaPaperReconstruction.Build("futures-live", [Open()], Start).Snapshot());
        Validate("ArenaPositionsRequest", ArenaPaperReconstruction.Build("futures-live", [], Start).Snapshot());
    }

    [Fact]
    public async Task Paper_outbox_restart_is_idempotent_and_does_not_modify_live_outbox()
    {
        string root = Path.Combine(Path.GetTempPath(), "arena-paper-" + Guid.NewGuid().ToString("N"));
        try
        {
            var live = new ArenaFileStore(root);
            var original = new ArenaFeedState();
            original.Trades.Add("kraken-real", new("kraken-real", "{}", Start, Start, Received: true));
            await live.SaveAsync(original, default);
            string before = await File.ReadAllTextAsync(Path.Combine(root, "outbox.json"));
            var paper = new ArenaFileStore(Path.Combine(root, "paper"));
            var handler = new LocalArena();
            using var http = new HttpClient(handler);
            var transport = new ArenaSubmission(http, "fixture", new Uri("http://127.0.0.1:49152"));
            var source = new PaperFixture();
            var feed = new ArenaFeed(source, transport, paper, _ => { });
            await feed.ReconcileAsync(default);
            await feed.DispatchAsync(default);
            var restart = new ArenaFeed(source, transport, new ArenaFileStore(Path.Combine(root, "paper")), _ => { });
            await restart.ReconcileAsync(default);
            await restart.DispatchAsync(default);
            Assert.Single(handler.Paths);
            Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(root, "outbox.json")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class PaperFixture : IArenaFeedSource
    {
        private readonly ArenaPaperHistory _history = ArenaPaperReconstruction.Build("futures-live", [Open(), Close()], Start.AddHours(1));
        public Task<IReadOnlyList<JsonObject>> ClosedAsync(CancellationToken ct) => Task.FromResult(_history.Closed);
        public Task<JsonObject> SnapshotAsync(CancellationToken ct) => Task.FromResult(_history.Snapshot());
    }

    private sealed class LocalArena : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.True(request.RequestUri!.IsLoopback);
            Paths.Add(request.RequestUri.AbsolutePath);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var row = body is JsonArray batch ? Assert.Single(batch)! : body;
            Assert.Equal("demo", row["account"]!.ToString());
            Assert.Null(row["declared_capital"]);
            int accepted = body is JsonArray ? 1 : 0;
            return new(HttpStatusCode.Accepted) { Content = new StringContent($"{{\"accepted\":{accepted},\"updated\":0}}") };
        }
    }
}
