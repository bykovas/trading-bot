using System.Net;
using System.Text;
using System.Text.Json;
using TradingBot.SpotWorker;
using Xunit;

namespace TradingBot.SpotWorker.Tests;

// Live execution ordering: the exchange order goes out FIRST and the virtual
// portfolio is committed only after the exchange accepted it. A failed or rejected
// live order must leave the portfolio untouched (no phantom position after a failed
// BUY, position kept after a failed SELL).
public class LiveOrderingTests
{
    [Fact]
    public async Task Failed_live_buy_leaves_the_virtual_portfolio_unchanged()
    {
        var outputDirectory = TempDir();
        var config = LiveConfig(outputDirectory);
        var worker = Worker(config, RejectingBrokerHandler());

        await worker.RunAsync(CancellationToken.None);

        var cycle = LastCycle(outputDirectory);
        var decision = cycle.GetProperty("decisions").EnumerateArray()
            .Single(d => d.GetProperty("pair").GetString() == "AAA/EUR");
        var action = decision.GetProperty("dryRunAction");

        Assert.Equal("LIVE_ORDER_FAILED", action.GetProperty("action").GetString());
        Assert.Contains("Insufficient funds", action.GetProperty("reason").GetString());

        // No phantom position, no phantom cash movement.
        var after = cycle.GetProperty("portfolioAfter");
        Assert.Empty(after.GetProperty("positions").EnumerateArray());
        Assert.Equal(
            cycle.GetProperty("portfolioBefore").GetProperty("cashEur").GetDecimal(),
            after.GetProperty("cashEur").GetDecimal());
    }

    [Fact]
    public async Task Accepted_live_buy_commits_the_virtual_fill()
    {
        var outputDirectory = TempDir();
        var config = LiveConfig(outputDirectory);
        var worker = Worker(config, AcceptingBrokerHandler());

        await worker.RunAsync(CancellationToken.None);

        var cycle = LastCycle(outputDirectory);
        var decision = cycle.GetProperty("decisions").EnumerateArray()
            .Single(d => d.GetProperty("pair").GetString() == "AAA/EUR");

        Assert.Equal("WOULD_BUY", decision.GetProperty("dryRunAction").GetProperty("action").GetString());
        Assert.Contains("LIVE_SUBMITTED", decision.GetProperty("broker").GetString());

        var positions = LastCycle(outputDirectory).GetProperty("portfolioAfter").GetProperty("positions").EnumerateArray().ToList();
        var position = Assert.Single(positions);
        Assert.Equal("AAA/EUR", position.GetProperty("pair").GetString());
    }

    // ------------------------------------------------------------------ plumbing

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "trading-bot-tests", Guid.NewGuid().ToString("N"));

    private static JsonElement LastCycle(string outputDirectory)
    {
        var line = File.ReadLines(Path.Combine(outputDirectory, "events.jsonl")).Last();
        return JsonDocument.Parse(line).RootElement.Clone();
    }

    private static DecisionWorker Worker(BotConfiguration config, HttpMessageHandler brokerHandler) => new(
        config,
        new FakeMarketDataSource("AAA/EUR"),
        new FixedAdvisor("AAA/EUR"),
        new IndicatorEngine(),
        new TechnicalDecisionEngine(),
        new RiskManager(),
        new DryRunPortfolio(config.DryRun, config.Portfolio, config.ExecutionPolicy, config.PositionExit, config.PositionSizing, strategy: config.Strategy),
        broker: new KrakenBroker(new HttpClient(brokerHandler), config.Kraken));

    // Live-enabled config whose synthetic series produces a firm buy (the fake data
    // source scores 0.35 with MinimumLongScore lowered to match). NOTE: this config
    // deliberately skips Normalize() so the price-action warm-up (forced on in real
    // live runs) does not block the single-cycle entry under test.
    private static BotConfiguration LiveConfig(string outputDirectory) => new()
    {
        Worker = new WorkerOptions { RunOnce = true },
        Kraken = new KrakenOptions
        {
            MarketDataMode = "sample",
            BaseUrl = "https://kraken.test.invalid",
            ApiKey = "test-key",
            ApiSecret = Convert.ToBase64String(Encoding.UTF8.GetBytes("test-secret-material"))
        },
        Ai = new AiOptions { Provider = "none", MaxRecommendations = 20, WatchlistRefreshSeconds = 0 },
        Trading = new TradingOptions { LiveTradingEnabled = true, MaxActiveInstruments = 20, TargetOrderEur = 10m },
        Risk = new RiskOptions { KillSwitch = false, MaxOrderEur = 10m, MaxOpenPositions = 5, MaxDailyLossEur = 5m, MaxTotalExposureEur = 50m },
        Strategy = new StrategyOptions
        {
            FastEmaPeriod = 3,
            SlowEmaPeriod = 5,
            RsiPeriod = 3,
            MinimumEmaGapPercent = 0m,
            MinimumLongScore = 0.35m,
            RequirePriceActionData = false
        },
        PositionSizing = new PositionSizingOptions { Enabled = false },
        DryRun = new DryRunOptions
        {
            Enabled = true,
            ApplyVirtualFills = true,
            OutputDirectory = outputDirectory,
            StateFile = "portfolio-state.json",
            EventsFile = "events.jsonl"
        },
        ExecutionPolicy = new ExecutionPolicyOptions
        {
            MaxNewPositionsPerCycle = 1,
            AllowImmediateExitOnSignalFlip = true,
            EntryBlackoutMinutes = 0,
            MaxNewPositionsPerHour = 0
        },
        PositionExit = new PositionExitOptions { MinProfitToExitOnSignalFlipPercent = 0m, StopLossPercent = 0m, TakeProfitPercent = 0m, MaxHoldMinutes = 0 },
        CandidateUniverse = new List<InstrumentOptions>
        {
            new() { Pair = "AAA/EUR", KrakenPair = "AAAEUR", Venue = "Kraken", Enabled = true }
        }
    };

    // Kraken private-API stub: Balance always succeeds; AddOrder rejects.
    private static HttpMessageHandler RejectingBrokerHandler() => new StubHandler(path =>
        path.Contains("AddOrder", StringComparison.Ordinal)
            ? """{"error":["EOrder:Insufficient funds"]}"""
            : """{"error":[],"result":{"ZEUR":"100.0"}}""");

    // Kraken private-API stub: AddOrder accepts with a txid.
    private static HttpMessageHandler AcceptingBrokerHandler() => new StubHandler(path =>
        path.Contains("AddOrder", StringComparison.Ordinal)
            ? """{"error":[],"result":{"txid":["TX-TEST-1"],"descr":{"order":"buy 7.14 AAAEUR @ market"}}}"""
            : """{"error":[],"result":{"ZEUR":"100.0"}}""");

    private sealed class StubHandler(Func<string, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request.RequestUri!.AbsolutePath), Encoding.UTF8, "application/json")
            });
    }

    private sealed class FixedAdvisor(params string[] pairs) : IWatchlistAdvisor
    {
        public Task<WatchlistAdvice> SelectAsync(
            IReadOnlyList<InstrumentMarketState> candidates,
            int maxRecommendations,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new WatchlistAdvice(
                "fixed",
                pairs.Take(maxRecommendations)
                    .Select((pair, index) => new WatchlistRecommendation(pair, index + 1, "fixed pick"))
                    .ToList(),
                Array.Empty<string>()));
        }
    }

    private sealed class FakeMarketDataSource(params string[] pairs) : IMarketDataSource
    {
        private readonly HashSet<string> _pairs = pairs.ToHashSet(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<InstrumentMarketState>> GetLightMarketStatesAsync(
            IReadOnlyList<InstrumentOptions> instruments,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<InstrumentMarketState>>(
                instruments.Select(instrument => State(instrument, includeCandles: false)).ToList());
        }

        public Task<IReadOnlyList<InstrumentMarketState>> GetFullMarketStatesAsync(
            IReadOnlyList<InstrumentOptions> instruments,
            int timeframeMinutes,
            IReadOnlyList<InstrumentMarketState> lightStates,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<InstrumentMarketState>>(
                instruments.Select(instrument => State(instrument, _pairs.Contains(instrument.Pair))).ToList());
        }

        private static InstrumentMarketState State(InstrumentOptions instrument, bool includeCandles)
        {
            var candles = includeCandles
                ? Enumerable.Range(0, 40)
                    .Select(index => new Candle(
                        DateTimeOffset.UtcNow.AddMinutes(-40 + index),
                        Open: 1m + index * 0.01m,
                        High: 1.02m + index * 0.01m,
                        Low: 0.99m + index * 0.01m,
                        Close: 1m + index * 0.01m,
                        Volume: 100m + index,
                        TradeCount: 10))
                    .ToArray()
                : Array.Empty<Candle>();

            return new InstrumentMarketState
            {
                Instrument = instrument,
                Candles = candles,
                Quote = new Quote(1.395m, 1.40m, 1.39m, 100m, 0m),
                PairRules = new PairRules(instrument.Pair, "online", 0.001m, 0.5m, 8, 2)
            };
        }
    }
}
