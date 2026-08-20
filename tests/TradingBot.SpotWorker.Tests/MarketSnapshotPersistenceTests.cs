using TradingBot.SpotWorker;
using Xunit;

namespace TradingBot.SpotWorker.Tests;

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

    [Fact]
    public async Task Extra_market_snapshot_polling_records_history_without_extra_decision_cycle()
    {
        var store = new FakeStore();
        var config = Config("AAA/EUR", "BBB/EUR");
        config.Worker = new WorkerOptions
        {
            RunOnce = false,
            LoopIntervalSeconds = 2,
            MarketSnapshotIntervalSeconds = 1
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var marketData = new CancellingMarketDataSource(cts, cancelAfterLightCalls: 2);
        var worker = Worker(config, store, marketData);

        await worker.RunAsync(cts.Token);

        Assert.Equal(1, store.AppendCycleCalls);
        Assert.Equal(2, store.SnapshotBatches.Count);
        Assert.All(store.SnapshotBatches, batch => Assert.Equal(2, batch.Count));
        Assert.Equal(1, marketData.FullCalls);
    }

    private static DecisionWorker Worker(BotConfiguration config, IDryRunPortfolioStore store) =>
        Worker(config, store, new FakeMarketDataSource());

    private static DecisionWorker Worker(BotConfiguration config, IDryRunPortfolioStore store, IMarketDataSource marketDataSource) => new(
        config,
        marketDataSource,
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

        public IReadOnlyList<MarketSnapshotRecord> LoadRecentMarketSnapshots(DateTimeOffset sinceUtc) =>
            SnapshotBatches.SelectMany(batch => batch).Where(snapshot => snapshot.Utc >= sinceUtc).ToList();

        public void SaveCashEvents(IReadOnlyList<PortfolioCashEvent> events) { }
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

    private class FakeMarketDataSource : IMarketDataSource
    {
        public int FullCalls { get; protected set; }

        public virtual Task<IReadOnlyList<InstrumentMarketState>> GetLightMarketStatesAsync(
            IReadOnlyList<InstrumentOptions> instruments,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<InstrumentMarketState>>(
                instruments.Select(instrument => State(instrument, includeCandles: false)).ToList());

        public Task<IReadOnlyList<InstrumentMarketState>> GetFullMarketStatesAsync(
            IReadOnlyList<InstrumentOptions> instruments,
            int timeframeMinutes,
            IReadOnlyList<InstrumentMarketState> lightStates,
            CancellationToken cancellationToken)
        {
            FullCalls++;
            return Task.FromResult<IReadOnlyList<InstrumentMarketState>>(
                instruments.Select(instrument => State(instrument, includeCandles: true)).ToList());
        }

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

    private sealed class CancellingMarketDataSource(
        CancellationTokenSource cancellation,
        int cancelAfterLightCalls) : FakeMarketDataSource
    {
        private int _lightCalls;

        public override Task<IReadOnlyList<InstrumentMarketState>> GetLightMarketStatesAsync(
            IReadOnlyList<InstrumentOptions> instruments,
            CancellationToken cancellationToken)
        {
            _lightCalls++;
            if (_lightCalls >= cancelAfterLightCalls)
            {
                cancellation.Cancel();
            }

            return base.GetLightMarketStatesAsync(instruments, cancellationToken);
        }
    }
}
