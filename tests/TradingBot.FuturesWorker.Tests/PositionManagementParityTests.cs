using System.Globalization;
using System.Net;
using System.Text.Json;
using TradingBot.Core.Indicators;
using TradingBot.Core.Scoring;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class PositionManagementParityTests
{
    private static readonly InstrumentOptions Instrument = new() { Pair = "ADA/USD", Enabled = true };
    private static readonly InstrumentOptions Candidate = new() { Pair = "ETH/USD", Enabled = true };

    [Theory]
    [InlineData(1, "2026-10-17T07:15:00.0004567Z", "2026-10-17T07:15:00Z")]
    [InlineData(15, "2026-10-17T07:14:59.9999999Z", "2026-10-17T07:00:00Z")]
    [InlineData(15, "2026-10-17T07:15:00.0004567Z", "2026-10-17T07:15:00Z")]
    public async Task Both_routes_request_the_same_trade_slice_preserving_quote_sources(int minutes, string now, string end)
    {
        var fixture = new Fixture(DateTimeOffset.Parse(now));
        var bykoLight = await fixture.Byko.GetLightMarketStatesAsync([Instrument], default);
        var pukoLight = await fixture.Puko.GetLightMarketStatesAsync([Instrument, Candidate], default);
        var byko = Assert.Single(await fixture.Byko.GetFullMarketStatesAsync([Instrument], minutes, bykoLight, default));
        var puko = (await fixture.Puko.GetFullMarketStatesAsync([Instrument, Candidate], minutes, pukoLight, default))[0];

        Assert.Equal(100m, byko.Quote!.MarkPrice);
        Assert.Equal(1m, puko.Quote!.MarkPrice);
        Assert.Equal(byko.Candles, puko.Candles);
        Assert.Equal(120, puko.Candles.Count);
        Assert.Equal(DateTimeOffset.Parse(end), puko.Candles[^1].OpenTime.AddMinutes(minutes));
        Assert.Equal(fixture.Requests[0], fixture.Requests[1]);
        Assert.All(fixture.Requests, request =>
        {
            using var json = JsonDocument.Parse(request);
            Assert.Equal("trade", json.RootElement.GetProperty("price_type").GetString());
            Assert.True(json.RootElement.GetProperty("closed_only").GetBoolean());
            Assert.Equal(0, json.RootElement.GetProperty("as_of").GetDateTimeOffset().Ticks % TimeSpan.TicksPerMillisecond);
        });
        Assert.All(fixture.EntrySource.FullPairs, pair => Assert.Equal(Candidate.Pair, pair));
        Assert.Equal(new[] { Instrument.Pair, Candidate.Pair }, fixture.EntrySource.LightPairs.Take(2));
    }

    [Fact]
    public async Task Missing_debyko_data_never_falls_back_to_mark_and_closed_positions_leave_the_route()
    {
        var fixture = new Fixture(DateTimeOffset.Parse("2026-10-17T07:15:00Z")) { Fail = true };
        var light = await fixture.Puko.GetLightMarketStatesAsync([Instrument], default);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Puko.GetFullMarketStatesAsync([Instrument], 15, light, default));
        Assert.Empty(fixture.EntrySource.FullPairs);

        fixture.Held.Clear();
        await fixture.Puko.GetFullMarketStatesAsync([Instrument], 15, light, default);
        Assert.Equal([Instrument.Pair], fixture.EntrySource.FullPairs);
    }

    // Replay the decision policy with identical position state and execution model;
    // exchange fills/order lifecycle are deliberately outside this data-parity contract.
    [Theory]
    [InlineData("LONG", "STOP_LOSS")]
    [InlineData("SHORT", "STOP_LOSS")]
    [InlineData("LONG", "TAKE_PROFIT")]
    [InlineData("SHORT", "TAKE_PROFIT")]
    [InlineData("LONG", "TRAILING_STOP")]
    [InlineData("SHORT", "TRAILING_STOP")]
    public async Task Same_position_and_market_path_produce_same_protection_and_exit_decisions(string side, string expected)
    {
        var fixture = new Fixture(DateTimeOffset.Parse("2026-10-17T07:15:00.1234567Z"));
        var bykoConfig = Config(FuturesEntrySignalSources.Debyko, expected == "TRAILING_STOP");
        var pukoConfig = Config(FuturesEntrySignalSources.BykoV2, expected == "TRAILING_STOP");
        var bykoPosition = Position(side);
        var pukoPosition = bykoPosition.Clone();
        var policies = new[] { new TpSlOrchestrator(bykoConfig), new TpSlOrchestrator(pukoConfig) };
        var offsets = expected switch
        {
            "STOP_LOSS" => new[] { 0m, -1m, -2m },
            "TAKE_PROFIT" => new[] { 0m, 2m, 4m },
            _ => new[] { 0m, 3m, 4m, 2m }
        };
        string? finalTrigger = null;
        foreach (var offset in offsets)
        {
            fixture.Price = 100m + (side == "SHORT" ? -offset : offset);
            fixture.EntrySource.Price = fixture.Price;
            var bykoLight = await fixture.Byko.GetLightMarketStatesAsync([Instrument], default);
            var pukoLight = await fixture.Puko.GetLightMarketStatesAsync([Instrument], default);
            var byko = Assert.Single(await fixture.Byko.GetFullMarketStatesAsync([Instrument], 15, bykoLight, default));
            var puko = Assert.Single(await fixture.Puko.GetFullMarketStatesAsync([Instrument], 15, pukoLight, default));
            var engine = new IndicatorEngine();
            var bykoIndicators = engine.Calculate(byko.Candles, bykoConfig.Strategy);
            var pukoIndicators = engine.Calculate(puko.Candles, pukoConfig.Strategy);
            Assert.Equal(bykoIndicators, pukoIndicators);
            Assert.Equal(AtrIndicator.CalculateLatestClosedAtr(byko.Candles, 14), AtrIndicator.CalculateLatestClosedAtr(puko.Candles, 14));
            Assert.Equal(new LongShortStrategy(bykoConfig).DecideHeld(bykoPosition, SignalScorer.Evaluate(byko, bykoIndicators, bykoConfig.Strategy)),
                new LongShortStrategy(pukoConfig).DecideHeld(pukoPosition, SignalScorer.Evaluate(puko, pukoIndicators, pukoConfig.Strategy)));

            var first = policies[0].Evaluate(bykoPosition, byko.Quote!.MarkPrice!.Value, byko.Quote.Last, byko.Quote.Bid, byko.Quote.Ask);
            var second = policies[1].Evaluate(pukoPosition, puko.Quote!.MarkPrice!.Value, puko.Quote.Last, puko.Quote.Bid, puko.Quote.Ask);
            Assert.Equal(first, second);
            Assert.Equal(bykoPosition.StopLossPrice, pukoPosition.StopLossPrice);
            Assert.Equal(bykoPosition.TakeProfitPrice, pukoPosition.TakeProfitPrice);
            Assert.Equal(bykoPosition.TrailingStopState, pukoPosition.TrailingStopState);
            Assert.Equal(bykoPosition.TrailingStopPercent, pukoPosition.TrailingStopPercent);
            Assert.Equal(bykoPosition.PeakPnlPercent, pukoPosition.PeakPnlPercent);
            finalTrigger = first?.Kind;
            fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(15);
        }
        Assert.Equal(expected, finalTrigger);
        Assert.Empty(fixture.EntrySource.FullPairs);
        Assert.Equal(FuturesEntrySignalSources.Debyko, bykoConfig.Futures.EntrySignalSource);
        Assert.Equal(FuturesEntrySignalSources.BykoV2, pukoConfig.Futures.EntrySignalSource);
    }

    [Theory]
    [InlineData("LONG", false)]
    [InlineData("SHORT", true)]
    public async Task Reversal_is_detected_by_both_routes(string side, bool rising)
    {
        var fixture = new Fixture(DateTimeOffset.Parse("2026-10-17T07:15:00Z")) { Rising = rising };
        fixture.EntrySource.Price = fixture.Price;
        foreach (var source in new[] { fixture.Byko, fixture.Puko })
        {
            var light = await source.GetLightMarketStatesAsync([Instrument], default);
            var state = Assert.Single(await source.GetFullMarketStatesAsync([Instrument], 15, light, default));
            var config = Config(FuturesEntrySignalSources.BykoV2, false);
            var indicators = new IndicatorEngine().Calculate(state.Candles, config.Strategy);
            var signal = SignalScorer.Evaluate(state, indicators, config.Strategy);
            Assert.Equal(FuturesDesiredExposure.Flat, new LongShortStrategy(config).DecideHeld(Position(side), signal));
        }
    }

    private static FuturesBotConfiguration Config(string entrySource, bool trailing) => new()
    {
        Futures = new() { EntrySignalSource = entrySource, LiveTradingEnabled = false },
        Strategy = new() { MinimumLongScore = 0.4m, MinimumEmaGapPercent = 0.01m },
        Exits = new() { AtrTrailingRegimeEnabled = trailing, TrailingActivationRMultiple = 1.5m, TrailingAtrMultiple = 1.5m },
        TpSl = new() { Enabled = true, StopLossPercent = 2m, TakeProfitPercent = 4m }
    };

    private static PortfolioPosition Position(string side) => new()
    {
        Pair = Instrument.Pair, Side = side, Origin = PositionOrigins.Bot, EntryPrice = 100m,
        Quantity = 1m, AtrPct = 0.5m, StopDistancePct = 2m,
        StopLossPrice = side == "SHORT" ? 102m : 98m,
        TakeProfitPrice = side == "SHORT" ? 96m : 104m,
        SlOrderState = "SIMULATED_OPEN", TpOrderState = "SIMULATED_OPEN"
    };

    private sealed class Fixture
    {
        public TestClock Clock { get; }
        public decimal Price = 100m;
        public bool Fail;
        public bool Rising = true;
        public HashSet<string> Held = [Instrument.Pair];
        public List<string> Requests = [];
        public EntrySource EntrySource = new();
        public IMarketDataSource Byko { get; }
        public IMarketDataSource Puko { get; }

        public Fixture(DateTimeOffset now)
        {
            Clock = new TestClock { UtcNow = now };
            var client = new DebykoMarketClient(new HttpClient(new Handler(Respond)) { BaseAddress = new Uri("https://debyko.invalid") }, "test-key");
            Byko = new DebykoPositionMarketDataSource(client, new(), Clock);
            Puko = new HeldPositionMarketDataSource(EntrySource, new DebykoPositionMarketDataSource(client, new(), Clock), () => Held);
        }

        private async Task<HttpResponseMessage> Respond(HttpRequestMessage request)
        {
            var quotePrice = Price.ToString(CultureInfo.InvariantCulture);
            if (request.RequestUri!.AbsolutePath == "/v2/snapshots")
                return Json(new { items = new[] { new { instrument = "ADA-PERP", venue = "KRAKEN-FUTURES", quote = "USD",
                    layers = new { mark = new { status = "present", values = new { mark = quotePrice } },
                        ticker = new { status = "present", values = new { last = quotePrice } },
                        book = new { status = "present", values = new { bid = quotePrice, ask = quotePrice } } } } } });
            Assert.Equal("/v2/candles", request.RequestUri.AbsolutePath);
            var raw = await request.Content!.ReadAsStringAsync();
            Requests.Add(raw);
            if (Fail) return new(HttpStatusCode.BadGateway) { Content = new StringContent("unavailable") };
            using var body = JsonDocument.Parse(raw);
            var query = body.RootElement;
            var from = query.GetProperty("from").GetDateTimeOffset();
            var to = query.GetProperty("to").GetDateTimeOffset();
            var asOf = query.GetProperty("as_of").GetDateTimeOffset();
            var minutes = (to - from).TotalMinutes / 120;
            return Json(new { data_as_of = asOf, from, to, timeframe = query.GetProperty("timeframe").GetString(),
                price_type = "trade", units = "published", items = new[] { new { instrument = "ADA-PERP",
                    venue = "KRAKEN-FUTURES", quote = "USD", status = "present",
                    candles = Enumerable.Range(0, 120).Select(i =>
                    {
                        var price = 100m + (Rising ? i : -i) * 0.1m;
                        return new { open_time = from.AddMinutes(minutes * i), close_time = from.AddMinutes(minutes * (i + 1)),
                            received_at = asOf, status = "present", open = price.ToString(CultureInfo.InvariantCulture),
                            high = (price + 0.2m).ToString(CultureInfo.InvariantCulture), low = (price - 0.2m).ToString(CultureInfo.InvariantCulture),
                            close = price.ToString(CultureInfo.InvariantCulture), volume = "10", trade_count = 1 };
                    }) } } });
        }
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
    private sealed class TestClock : IClock { public DateTimeOffset UtcNow { get; set; } }
    private sealed class EntrySource : IMarketDataSource
    {
        public decimal Price = 1m;
        public List<string> LightPairs = [];
        public List<string> FullPairs = [];
        public Task<IReadOnlyList<InstrumentMarketState>> GetLightMarketStatesAsync(IReadOnlyList<InstrumentOptions> instruments, CancellationToken cancellationToken)
        {
            LightPairs.AddRange(instruments.Select(x => x.Pair));
            return Task.FromResult<IReadOnlyList<InstrumentMarketState>>(instruments.Select(x => new InstrumentMarketState
                { Instrument = x, Candles = [], Quote = new Quote(Price, Price, Price, 0m, MarkPrice: Price) }).ToArray());
        }
        public Task<IReadOnlyList<InstrumentMarketState>> GetFullMarketStatesAsync(IReadOnlyList<InstrumentOptions> instruments, int timeframeMinutes,
            IReadOnlyList<InstrumentMarketState> lightStates, CancellationToken cancellationToken)
        {
            FullPairs.AddRange(instruments.Select(x => x.Pair));
            return GetLightMarketStatesAsync(instruments, cancellationToken);
        }
    }
}
