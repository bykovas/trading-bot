using TradingBot.Worker;
using Xunit;

namespace TradingBot.Worker.Tests;

// Verifies the per-cycle light market snapshot persistence: one row per universe pair
// per cycle through the store abstraction, and that a store failure never fails the cycle.
public class MarketSnapshotPersistenceTests
{
    [Fact]
    public async Task Persists_one_snapshot_row_per_universe_pair_each_cycle()
    {
        var store = new FakeStore();
        var config = Config("AAA/EUR", "BBB/EUR", "CCC/EUR");
        var worker = Worker(config, store);

        await worker.RunAsync(CancellationToken.None);

        var batch = Assert.Single(store.SnapshotBatches);
        Assert.Equal(3, batch.Count);
        Assert.Equal(
            new[] { "AAA/EUR", "BBB/EUR", "CCC/EUR" },
            batch.Select(row => row.Pair).OrderBy(pair => pair).ToArray());

        // Every row shares the cycle id and carries the light bid/ask/last it saw.
        Assert.Single(batch.Select(row => row.CycleId).Distinct());
        Assert.All(batch, row =>
        {
            Assert.Equal(1m, row.Bid);
            Assert.Equal(1.01m, row.Ask);
            Assert.Equal(1m, row.Last);
        });
    }

    [Fact]
    public async Task Snapshot_store_failure_does_not_fail_the_cycle()
    {
        var store = new FakeStore { ThrowOnSnapshots = true };
        var config = Config("AAA/EUR", "BBB/EUR");
        var worker = Worker(config, store);

        // Must complete without throwing despite the snapshot write blowing up...
        await worker.RunAsync(CancellationToken.None);

        // ...and the rest of the cycle still ran (the cycle record was written).
        Assert.Equal(1, store.AppendCycleCalls);
        Assert.False(config.Risk.KillSwitch);
    }

    private static DecisionWorker Worker(BotConfiguration config, IDryRunPortfolioStore store) => new(
        config,
        new FakeMarketDataSource(),
        new FixedAdvisor("AAA/EUR"),
        new IndicatorEngine(),
        new TechnicalDecisionEngine(),
        new RiskManager(),
        new DryRunPortfolio(config.DryRun, config.Portfolio, config.ExecutionPolicy, config.PositionExit, config.PositionSizing, store: store),
        broker: null);

    private static BotConfiguration Config(params string[] pairs) => new()
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
            OutputDirectory = Path.Combine(Path.GetTempPath(), "trading-bot-tests", Guid.NewGuid().ToString("N"))
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

    private sealed class FakeStore : IDryRunPortfolioStore
    {
        public List<IReadOnlyList<MarketSnapshotRecord>> SnapshotBatches { get; } = new();
        public int AppendCycleCalls { get; private set; }
        public bool ThrowOnSnapshots { get; init; }

        public string StateDescription => "fake:state";
        public string EventsDescription => "fake:events";

        public PortfolioState? Load() => null;

        public void Save(PortfolioState state)
        {
        }

        public void AppendCycle(DryRunCycleRecord record) => AppendCycleCalls++;

        public void AppendMarketSnapshots(IReadOnlyList<MarketSnapshotRecord> snapshots)
        {
            if (ThrowOnSnapshots)
            {
                throw new InvalidOperationException("simulated snapshot store failure");
            }

            SnapshotBatches.Add(snapshots);
        }
    }

    private sealed class FixedAdvisor(params string[] pairs) : IWatchlistAdvisor
    {
        public Task<WatchlistAdvice> SelectAsync(
            IReadOnlyList<InstrumentMarketState> candidates,
            int maxRecommendations,
            CancellationToken cancellationToken) =>
            Task.FromResult(new WatchlistAdvice(
                "fixed",
                pairs.Take(maxRecommendations)
                    .Select((pair, index) => new WatchlistRecommendation(pair, index + 1, "fixed pick"))
                    .ToList(),
                Array.Empty<string>()));
    }

    private sealed class FakeMarketDataSource : IMarketDataSource
    {
        public Task<IReadOnlyList<InstrumentMarketState>> GetLightMarketStatesAsync(
            IReadOnlyList<InstrumentOptions> instruments,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<InstrumentMarketState>>(
                instruments.Select(instrument => State(instrument, includeCandles: false)).ToList());

        public Task<IReadOnlyList<InstrumentMarketState>> GetFullMarketStatesAsync(
            IReadOnlyList<InstrumentOptions> instruments,
            int timeframeMinutes,
            IReadOnlyList<InstrumentMarketState> lightStates,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<InstrumentMarketState>>(
                instruments.Select(instrument => State(instrument, includeCandles: true)).ToList());

        private static InstrumentMarketState State(InstrumentOptions instrument, bool includeCandles)
        {
            var candles = includeCandles
                ? Enumerable.Range(0, 40)
                    .Select(index => new Candle(
                        DateTimeOffset.UtcNow.AddMinutes(-40 + index),
                        1m + index * 0.01m,
                        1.02m + index * 0.01m,
                        0.99m + index * 0.01m,
                        1m + index * 0.01m,
                        100m + index,
                        10))
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
