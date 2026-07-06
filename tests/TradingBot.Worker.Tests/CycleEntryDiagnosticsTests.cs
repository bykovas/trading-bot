using System.Text.Json;
using TradingBot.Worker;
using Xunit;

namespace TradingBot.Worker.Tests;

// End-to-end checks that every persisted cycle carries the entry-funnel
// diagnostics: candidate counters, top candidates with rejection reasons, excluded
// pairs, and an explicit chosen-pair or no-trade reason. Silent inactivity is a bug.
public class CycleEntryDiagnosticsTests
{
    [Fact]
    public async Task No_trade_cycle_explains_itself_in_the_cycle_record()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "trading-bot-tests", Guid.NewGuid().ToString("N"));
        var config = Config(outputDirectory, "AAA/EUR", "BBB/EUR", "CCC/EUR");
        // Firm threshold nothing can reach and exploratory disabled -> guaranteed no-trade.
        config.Strategy.MinimumLongScore = 0.99m;
        config.Strategy.ExploratoryEntriesEnabled = false;

        // Advisor only selects two of three pairs, so CCC/EUR must appear as excluded.
        var worker = Worker(config, new FakeMarketDataSource("AAA/EUR", "BBB/EUR", "CCC/EUR"), new FixedAdvisor("AAA/EUR", "BBB/EUR"));
        await worker.RunAsync(CancellationToken.None);

        var cycleLine = File.ReadLines(Path.Combine(outputDirectory, "events.jsonl")).Last();
        using var doc = JsonDocument.Parse(cycleLine);
        var diagnostics = doc.RootElement.GetProperty("entryDiagnostics");

        Assert.Equal(3, diagnostics.GetProperty("snapshotPairsAvailable").GetInt32());
        Assert.Equal(2, diagnostics.GetProperty("activePairsEvaluated").GetInt32());
        Assert.Equal(2, diagnostics.GetProperty("entryPairsEvaluated").GetInt32());
        Assert.Equal(0, diagnostics.GetProperty("eligibleEntryCandidates").GetInt32());
        Assert.Equal(JsonValueKind.Null, diagnostics.GetProperty("chosenPair").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(diagnostics.GetProperty("noTradeReason").GetString()));

        var topCandidates = diagnostics.GetProperty("topCandidates").EnumerateArray().ToList();
        Assert.NotEmpty(topCandidates);
        foreach (var candidate in topCandidates)
        {
            Assert.False(string.IsNullOrWhiteSpace(candidate.GetProperty("rejectionReason").GetString()));
            Assert.True(candidate.TryGetProperty("hasBullishStructure", out _));
            Assert.True(candidate.TryGetProperty("emaFullyConfirmed", out _));
            Assert.True(candidate.TryGetProperty("bullishEmaGapPercent", out _));
            Assert.True(candidate.TryGetProperty("emaGapVelocityPercent", out _));
            Assert.True(candidate.TryGetProperty("earlyEntryEligible", out _));
            Assert.True(candidate.TryGetProperty("earlyEntryReason", out _));
            Assert.True(candidate.TryGetProperty("earlyEntryDiagnosticScore", out _));
            Assert.True(candidate.TryGetProperty("earlyEntrySuggestedNotionalEur", out _));
        }

        var excluded = diagnostics.GetProperty("excludedPairs").EnumerateArray().ToList();
        var excludedPair = Assert.Single(excluded);
        Assert.Equal("CCC/EUR", excludedPair.GetProperty("pair").GetString());
        Assert.Contains("REJECT_ACTIVE_PAIR_FILTER", excludedPair.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Trade_cycle_records_the_chosen_pair()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "trading-bot-tests", Guid.NewGuid().ToString("N"));
        var config = Config(outputDirectory, "AAA/EUR");
        // The synthetic rising series scores 0.35 (overheated RSI + elevated
        // volatility eat the bonuses); anything at or below that produces a buy.
        config.Strategy.MinimumLongScore = 0.35m;

        var worker = Worker(config, new FakeMarketDataSource("AAA/EUR"), new FixedAdvisor("AAA/EUR"));
        await worker.RunAsync(CancellationToken.None);

        var cycleLine = File.ReadLines(Path.Combine(outputDirectory, "events.jsonl")).Last();
        using var doc = JsonDocument.Parse(cycleLine);
        var diagnostics = doc.RootElement.GetProperty("entryDiagnostics");

        Assert.Equal("AAA/EUR", diagnostics.GetProperty("chosenPair").GetString());
        Assert.Equal(JsonValueKind.Null, diagnostics.GetProperty("noTradeReason").ValueKind);
    }

    [Fact]
    public async Task Strong_mover_backfill_evaluates_clean_mover_not_selected_by_advisor()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "trading-bot-tests", Guid.NewGuid().ToString("N"));
        var config = Config(outputDirectory, "AAA/EUR", "BBB/EUR", "CCC/EUR");
        config.Trading.StrongMoverBackfillEnabled = true;
        config.Trading.StrongMoverMinChangePercent = 4m;
        config.Trading.StrongMoverMaxSpreadPercent = 0.35m;
        config.Trading.StrongMoverMinDailyVolumeEur = 100_000m;
        config.Trading.StrongMoverMaxBackfillPairs = 5;

        var worker = Worker(
            config,
            new StrongMoverMarketDataSource("CCC/EUR"),
            new FixedAdvisor("AAA/EUR", "BBB/EUR"));
        await worker.RunAsync(CancellationToken.None);

        var cycleLine = File.ReadLines(Path.Combine(outputDirectory, "events.jsonl")).Last();
        using var doc = JsonDocument.Parse(cycleLine);
        var root = doc.RootElement;
        var diagnostics = root.GetProperty("entryDiagnostics");

        Assert.Equal(3, diagnostics.GetProperty("activePairsEvaluated").GetInt32());
        Assert.Contains(root.GetProperty("decisions").EnumerateArray(), decision =>
            decision.GetProperty("pair").GetString() == "CCC/EUR");
        Assert.DoesNotContain(diagnostics.GetProperty("excludedPairs").EnumerateArray(), excluded =>
            excluded.GetProperty("pair").GetString() == "CCC/EUR");
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
        new DryRunPortfolio(config.DryRun, config.Portfolio, config.ExecutionPolicy, config.PositionExit, config.PositionSizing, strategy: config.Strategy),
        broker: null);

    private static BotConfiguration Config(string outputDirectory, params string[] pairs) => new()
    {
        Worker = new WorkerOptions { RunOnce = true },
        Kraken = new KrakenOptions { MarketDataMode = "sample" },
        Ai = new AiOptions { Provider = "none", MaxRecommendations = 20, WatchlistRefreshSeconds = 0 },
        Trading = new TradingOptions { MaxActiveInstruments = 20, TargetOrderEur = 10m },
        Risk = new RiskOptions { MaxOrderEur = 10m, MaxOpenPositions = 5, MaxDailyLossEur = 5m, MaxTotalExposureEur = 50m },
        Strategy = new StrategyOptions
        {
            FastEmaPeriod = 3,
            SlowEmaPeriod = 5,
            RsiPeriod = 3,
            MinimumEmaGapPercent = 0m,
            MinimumLongScore = 0.65m,
            // Single-cycle runs never accumulate snapshot history, so the guard
            // abstains; keep defaults for the rest.
            PriceActionMinSnapshots = 4
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

    private sealed class StrongMoverMarketDataSource(string strongPair) : IMarketDataSource
    {
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
                instruments.Select(instrument => State(instrument, includeCandles: true)).ToList());
        }

        private InstrumentMarketState State(InstrumentOptions instrument, bool includeCandles)
        {
            var isStrong = instrument.Pair.Equals(strongPair, StringComparison.OrdinalIgnoreCase);
            var last = isStrong ? 1.08m : 1.00m;
            var candles = includeCandles
                ? Enumerable.Range(0, 40)
                    .Select(index => new Candle(
                        DateTimeOffset.UtcNow.AddMinutes(-40 + index),
                        Open: 1m,
                        High: 1.01m,
                        Low: 0.99m,
                        Close: 1m,
                        Volume: 100m,
                        TradeCount: 10))
                    .ToArray()
                : Array.Empty<Candle>();

            return new InstrumentMarketState
            {
                Instrument = instrument,
                Candles = candles,
                Quote = new Quote(last * 0.999m, last * 1.001m, last, isStrong ? 200_000m : 100m, isStrong ? 8m : 0m),
                PairRules = new PairRules(instrument.Pair, "online", 0.001m, 0.5m, 8, 2)
            };
        }
    }
}
