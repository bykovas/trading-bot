using TradingBot.SpotWorker;
using Xunit;

namespace TradingBot.SpotWorker.Tests;

public class DecisionWorkerMarketDataFlowTests
{
    [Fact]
    public async Task Open_position_is_fetched_and_evaluated_even_when_absent_from_watchlist()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "trading-bot-tests", Guid.NewGuid().ToString("N"));
        var config = Config(outputDirectory, "AAA/EUR", "HELD/EUR");
        config.Portfolio.Positions.Add(new PositionOptions
        {
            Pair = "HELD/EUR",
            Side = "LONG",
            Quantity = 1m,
            EntryPrice = 1m,
            EntryNotionalEur = 1m
        });

        var marketData = new FakeMarketDataSource(
            fullCandles: new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["AAA/EUR"] = true,
                ["HELD/EUR"] = true
            });

        var worker = Worker(config, marketData, new FixedAdvisor("AAA/EUR"));
        await worker.RunAsync(CancellationToken.None);

        Assert.Contains("AAA/EUR", marketData.FullRequestedPairs);
        Assert.Contains("HELD/EUR", marketData.FullRequestedPairs);
        var eventsJson = File.ReadAllText(Path.Combine(outputDirectory, "events.jsonl"));
        Assert.Contains("\"pair\":\"HELD/EUR\"", eventsJson);
        Assert.Contains("\"worker\":{\"version\":\"test-version\"", eventsJson);
    }

    [Fact]
    public async Task Ticker_only_pair_is_not_written_as_a_decision()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "trading-bot-tests", Guid.NewGuid().ToString("N"));
        var config = Config(outputDirectory, "AAA/EUR");
        var marketData = new FakeMarketDataSource(
            fullCandles: new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["AAA/EUR"] = false
            });

        var worker = Worker(config, marketData, new FixedAdvisor("AAA/EUR"));
        await worker.RunAsync(CancellationToken.None);

        var eventsJson = File.ReadAllText(Path.Combine(outputDirectory, "events.jsonl"));
        Assert.DoesNotContain("\"pair\":\"AAA/EUR\"", eventsJson);
    }

    private static DecisionWorker Worker(
        BotConfiguration config,
        IMarketDataSource marketData,
        IWatchlistAdvisor advisor) => new(
        config,
        marketData,
        advisor,
        new IndicatorEngine(),
        new TechnicalDecisionEngine(),
        new RiskManager(),
        new DryRunPortfolio(config.DryRun, config.Portfolio, config.ExecutionPolicy, config.PositionExit, config.PositionSizing),
        broker: null,
        buildInfo: new WorkerBuildInfo(
            "test-version",
            "test-commit",
            "2026-07-04T00:00:00Z",
            "test-image",
            "test-strategy",
            "test-change-set"));

    private static BotConfiguration Config(string outputDirectory, params string[] pairs) => new()
    {
        Worker = new WorkerOptions { RunOnce = true },
        Kraken = new KrakenOptions { MarketDataMode = "sample" },
        Ai = new AiOptions { Provider = "none", MaxRecommendations = 20, WatchlistRefreshSeconds = 0 },
        Trading = new TradingOptions { MaxActiveInstruments = 20, TargetOrderEur = 10m },
        Risk = new RiskOptions { MaxOrderEur = 10m, MaxOpenPositions = 5, MaxDailyLossEur = 5m, MaxTotalExposureEur = 50m },
        Strategy = new StrategyOptions { FastEmaPeriod = 3, SlowEmaPeriod = 5, RsiPeriod = 3, MinimumEmaGapPercent = 0m, MinimumLongScore = 0.65m },
        PositionSizing = new PositionSizingOptions { Enabled = false },
        DryRun = new DryRunOptions
        {
            Enabled = true,
            ApplyVirtualFills = true,
            OutputDirectory = outputDirectory,
            StateFile = "portfolio-state.json",
            EventsFile = "events.jsonl"
        },
        ExecutionPolicy = new ExecutionPolicyOptions { MaxNewPositionsPerCycle = 1, AllowImmediateExitOnSignalFlip = true },
        PositionExit = new PositionExitOptions { MinProfitToExitOnSignalFlipPercent = 0m, StopLossPercent = 0m, TakeProfitPercent = 0m, MaxHoldMinutes = 0 },
        CandidateUniverse = pairs.Select(pair => new InstrumentOptions
        {
            Pair = pair,
            KrakenPair = pair.Replace("/", string.Empty, StringComparison.Ordinal),
            Venue = "Kraken",
            Enabled = true
        }).ToList()
    };

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

    private sealed class FakeMarketDataSource(IReadOnlyDictionary<string, bool> fullCandles) : IMarketDataSource
    {
        public List<string> FullRequestedPairs { get; } = new();

        public Task<IReadOnlyList<InstrumentMarketState>> GetLightMarketStatesAsync(
            IReadOnlyList<InstrumentOptions> instruments,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<InstrumentMarketState>>(instruments.Select(instrument => State(instrument, includeCandles: false)).ToList());
        }

        public Task<IReadOnlyList<InstrumentMarketState>> GetFullMarketStatesAsync(
            IReadOnlyList<InstrumentOptions> instruments,
            int timeframeMinutes,
            IReadOnlyList<InstrumentMarketState> lightStates,
            CancellationToken cancellationToken)
        {
            FullRequestedPairs.AddRange(instruments.Select(instrument => instrument.Pair));
            return Task.FromResult<IReadOnlyList<InstrumentMarketState>>(instruments
                .Select(instrument => State(instrument, fullCandles.TryGetValue(instrument.Pair, out var includeCandles) && includeCandles))
                .ToList());
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
                Quote = new Quote(1m, 1.01m, 1m, 100m, 0m),
                PairRules = new PairRules(instrument.Pair, "online", 0.001m, 0.5m, 8, 2),
                DataWarning = includeCandles ? null : "ticker-only test data"
            };
        }
    }
}
