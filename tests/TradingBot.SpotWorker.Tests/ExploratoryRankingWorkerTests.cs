using System.Text.Json;
using TradingBot.SpotWorker;
using Xunit;

namespace TradingBot.SpotWorker.Tests;

// Worker-level check of the exploratory top-N rank cut: with THREE simultaneous
// exploratory candidates and ExploratoryMaxRank = 2, exactly two may enter and the
// third must be recorded as WOULD_BUY_BLOCKED / EXPLORATORY_RANK. Runs the real
// DecisionWorker over several cycles so the snapshot history genuinely warms up and
// the candidates carry positive price action.
public class ExploratoryRankingWorkerTests
{
    [Fact]
    public async Task Third_exploratory_candidate_is_cut_by_the_rank_limit()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "trading-bot-tests", Guid.NewGuid().ToString("N"));
        var config = Config(outputDirectory, "AAA/EUR", "BBB/EUR", "CCC/EUR");

        var marketData = new RisingMarketDataSource("AAA/EUR", "BBB/EUR", "CCC/EUR");
        var worker = new DecisionWorker(
            config,
            marketData,
            new FixedAdvisor("AAA/EUR", "BBB/EUR", "CCC/EUR"),
            new IndicatorEngine(),
            new TechnicalDecisionEngine(),
            new RiskManager(),
            new DryRunPortfolio(config.DryRun, config.Portfolio, config.ExecutionPolicy, config.PositionExit, config.PositionSizing, strategy: config.Strategy),
            broker: null);

        // Warm-up cycles: exploratory disabled, firm threshold unreachable -> no
        // trades, but the rising snapshot history accumulates on the SAME worker.
        config.Strategy.ExploratoryEntriesEnabled = false;
        for (var i = 0; i < 5; i++)
        {
            await worker.RunAsync(CancellationToken.None);
        }

        // Final cycle: all three pairs are exploratory candidates with identical
        // scores; ranking falls through to stable input order (AAA, BBB, CCC).
        config.Strategy.ExploratoryEntriesEnabled = true;
        await worker.RunAsync(CancellationToken.None);

        var cycleLine = File.ReadLines(Path.Combine(outputDirectory, "events.jsonl")).Last();
        using var doc = JsonDocument.Parse(cycleLine);
        var decisions = doc.RootElement.GetProperty("decisions").EnumerateArray().ToList();

        string ActionOf(string pair) => decisions
            .Single(d => d.GetProperty("pair").GetString() == pair)
            .GetProperty("dryRunAction").GetProperty("action").GetString()!;

        Assert.Equal("WOULD_BUY", ActionOf("AAA/EUR"));
        Assert.Equal("WOULD_BUY", ActionOf("BBB/EUR"));
        Assert.Equal("WOULD_BUY_BLOCKED", ActionOf("CCC/EUR"));

        var blocked = decisions.Single(d => d.GetProperty("pair").GetString() == "CCC/EUR");
        Assert.Equal("EXPLORATORY_RANK", blocked.GetProperty("dryRunAction").GetProperty("holdReasonCode").GetString());
        Assert.Equal(EntryRejection.ExploratoryRank, blocked.GetProperty("entryRejectionReason").GetString());
        Assert.True(blocked.GetProperty("exploratory").GetBoolean());
    }

    private static BotConfiguration Config(string outputDirectory, params string[] pairs) => new()
    {
        Worker = new WorkerOptions { RunOnce = true },
        Kraken = new KrakenOptions { MarketDataMode = "sample" },
        Ai = new AiOptions { Provider = "none", MaxRecommendations = 20, WatchlistRefreshSeconds = 0 },
        Trading = new TradingOptions { MaxActiveInstruments = 20, TargetOrderEur = 10m },
        Portfolio = new PortfolioOptions { StartingCashEur = 100m },
        Risk = new RiskOptions { MaxOrderEur = 10m, MaxOpenPositions = 5, MaxDailyLossEur = 5m, MaxTotalExposureEur = 50m },
        Strategy = new StrategyOptions
        {
            FastEmaPeriod = 3,
            SlowEmaPeriod = 5,
            RsiPeriod = 3,
            MinimumEmaGapPercent = 0m,
            // Firm entries impossible; the synthetic series scores 0.35, which is
            // exactly the exploratory bar.
            MinimumLongScore = 0.90m,
            ExploratoryEntriesEnabled = false,
            ExploratoryMinimumLongScore = 0.35m,
            ExploratoryMaxRank = 2,
            PriceActionLookbackSnapshots = 6,
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
            // Big enough that ONLY the exploratory rank limit can cut candidates.
            MaxNewPositionsPerCycle = 5,
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

    // Market data whose ticker rises ~1% per cycle, so the recorded snapshot series
    // reads as genuinely RISING price action. Candles stay identical (score 0.35).
    private sealed class RisingMarketDataSource(params string[] pairs) : IMarketDataSource
    {
        private readonly HashSet<string> _pairs = pairs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        private int _cycle;

        public Task<IReadOnlyList<InstrumentMarketState>> GetLightMarketStatesAsync(
            IReadOnlyList<InstrumentOptions> instruments,
            CancellationToken cancellationToken)
        {
            _cycle++;
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

        private InstrumentMarketState State(InstrumentOptions instrument, bool includeCandles)
        {
            var last = 1.39m * (1m + 0.01m * _cycle);
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
                Quote = new Quote(last * 0.999m, last * 1.001m, last, 100m, 0m),
                PairRules = new PairRules(instrument.Pair, "online", 0.001m, 0.5m, 8, 2)
            };
        }
    }
}
