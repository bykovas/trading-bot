using System.Text.Json;

using Xunit;

using TradingBot.SpotWorker;

namespace TradingBot.SpotWorker.Tests;

// Price-action warm-up visibility and safety:
//   - the assessment exposes an explicit warm-up state (INSUFFICIENT_DATA,
//     WARMING_UP, READY, STALE) with sample counts and timestamps;
//   - UNKNOWN price action never silently passes for a safe condition: live mode
//     blocks firm EMA/RSI entries, exploratory mode demands KNOWN positive action;
//   - near-threshold candidates get PRECISE rejection reasons instead of a generic
//     REJECT_SCORE_BELOW_THRESHOLD;
//   - exploratory entries respect the stricter MaxExploratorySpreadPercent;
//   - startup hydration from persisted market snapshots removes the restart
//     blindness without bridging long downtime gaps.
public class PriceActionWarmupTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-04T12:00:00Z");

    private static StrategyOptions Strategy() => new()
    {
        MinimumLongScore = 0.90m,
        MaxEntrySpreadPercent = 0.5m,
        MaxExploratorySpreadPercent = 0.30m,
        ExploratoryMinimumLongScore = 0.85m,
        PriceActionLookbackSnapshots = 6,
        PriceActionMinSnapshots = 4,
        PriceActionMaxDeclinePercent = 0.5m,
        PriceActionMaxNonRisingSnapshots = 3,
        PriceActionMaxSampleAgeMinutes = 30
    };

    private static TechnicalSignal Signal(
        decimal score,
        decimal? uncapped = null,
        bool volumeConfirmed = false,
        decimal momentum = 0m,
        decimal volume = 0m,
        bool allowsLong = true,
        bool emaFullyConfirmed = true,
        decimal? bullishEmaGapPercent = 0.4m,
        decimal? emaGapVelocityPercent = null) =>
        new(
            score,
            "LONG_BIAS",
            AllowsLong: allowsLong,
            HasBullishStructure: true,
            EmaFullyConfirmed: emaFullyConfirmed,
            BullishEmaGapPercent: bullishEmaGapPercent,
            EmaGapVelocityPercent: emaGapVelocityPercent,
            Contributions:
            [
                new SignalContribution("EMA", 0.30m, "bullish cross"),
                new SignalContribution("RSI", 0.15m, "ideal band"),
                new SignalContribution("Momentum", momentum, momentum > 0m ? "recent push" : "no recent push"),
                new SignalContribution("Volume", volume, volume > 0m ? "above average" : "below confirmation multiple")
            ],
            UncappedScore: uncapped ?? score,
            VolumeConfirmed: volumeConfirmed);

    private static InstrumentMarketState MarketState(decimal bid, decimal ask, decimal last) => new()
    {
        Instrument = new InstrumentOptions { Pair = "TEST/EUR", KrakenPair = "TESTEUR", Venue = "Kraken", Enabled = true },
        Candles = [new Candle(Now, last, last, last, last, 100m, 10)],
        Quote = new Quote(bid, ask, last, 1_000_000m),
        PairRules = new PairRules("TEST/EUR", "online", 0.001m, 0.5m, 8, 5)
    };

    private static PriceActionAssessment Positive() =>
        new("TEST/EUR", 6, true, 1.001m, 0.4m, 0.999m, 0, 4, Now.AddMinutes(-25), Now);

    private static SnapshotPriceHistory HistoryWith(int samples, DateTimeOffset newestUtc)
    {
        var history = new SnapshotPriceHistory();
        for (var i = 0; i < samples; i++)
        {
            var utc = newestUtc.AddMinutes(-(samples - 1 - i) * 5);
            var last = 1.0m + i * 0.001m;
            history.Record("TEST/EUR", new PriceObservation(utc, last, last - 0.0005m, last + 0.0005m));
        }

        return history;
    }

    // ---------------------------------------------------------------- warm-up states

    [Fact]
    public void No_observations_at_all_reads_as_INSUFFICIENT_DATA()
    {
        var assessment = new SnapshotPriceHistory().Assess("TEST/EUR", 6, 4, Now, 30);

        Assert.Null(assessment);
        Assert.Equal(PriceActionAssessment.StateInsufficientData, PriceActionAssessment.WarmupStateOf(assessment));
    }

    [Fact]
    public void Fewer_samples_than_required_reads_as_WARMING_UP_with_counts_and_timestamps()
    {
        var assessment = HistoryWith(samples: 2, newestUtc: Now).Assess("TEST/EUR", 6, 4, Now, 30);

        Assert.NotNull(assessment);
        Assert.Equal(PriceActionAssessment.StateWarmingUp, assessment!.WarmupState);
        Assert.Equal("UNKNOWN", assessment.Direction);
        Assert.Null(assessment.TrendPercent);
        Assert.Equal(2, assessment.SnapshotCount);
        Assert.Equal(4, assessment.SamplesRequired);
        Assert.Equal(Now.AddMinutes(-5), assessment.OldestSampleUtc);
        Assert.Equal(Now, assessment.NewestSampleUtc);
    }

    [Fact]
    public void Enough_fresh_samples_reads_as_READY_with_a_known_direction()
    {
        var assessment = HistoryWith(samples: 5, newestUtc: Now).Assess("TEST/EUR", 6, 4, Now, 30);

        Assert.NotNull(assessment);
        Assert.Equal(PriceActionAssessment.StateReady, assessment!.WarmupState);
        Assert.True(assessment.DataSufficient);
        Assert.Equal("RISING", assessment.Direction);
    }

    [Fact]
    public void Enough_samples_but_an_outdated_newest_one_reads_as_STALE_and_insufficient()
    {
        // 5 samples, but the newest is 40 minutes old (limit 30): the series must
        // not pass for a live read of the market.
        var assessment = HistoryWith(samples: 5, newestUtc: Now.AddMinutes(-40)).Assess("TEST/EUR", 6, 4, Now, 30);

        Assert.NotNull(assessment);
        Assert.Equal(PriceActionAssessment.StateStale, assessment!.WarmupState);
        Assert.False(assessment.DataSufficient);
        Assert.Equal("UNKNOWN", assessment.Direction);
    }

    // ------------------------------------------------------- warm-up safety rules

    [Fact]
    public void Live_mode_blocks_a_firm_EMA_RSI_entry_while_price_action_is_UNKNOWN()
    {
        // Right after a restart the history is empty. A 0.95 score built from
        // lagging EMA/RSI must NOT open a live position while the guard is blind.
        var strategy = Strategy();
        strategy.RequirePriceActionData = true; // what Normalize() forces in live mode

        var gate = EntryGate.Evaluate(
            Signal(0.95m, volumeConfirmed: true, momentum: 0.10m, volume: 0.05m),
            MarketState(bid: 0.999m, ask: 1.001m, last: 1.0m),
            priceAction: null,
            strategy,
            liveMode: true);

        Assert.Equal("NONE", gate.DesiredPosition);
        Assert.Equal(EntryRejection.InsufficientPriceHistory, gate.RejectionReason);
    }

    [Fact]
    public void Warmup_requirement_also_blocks_STALE_price_action()
    {
        var strategy = Strategy();
        strategy.RequirePriceActionData = true;

        var stale = HistoryWith(samples: 5, newestUtc: Now.AddMinutes(-40)).Assess("TEST/EUR", 6, 4, Now, 30);
        var gate = EntryGate.Evaluate(
            Signal(0.95m, volumeConfirmed: true, momentum: 0.10m, volume: 0.05m),
            MarketState(bid: 0.999m, ask: 1.001m, last: 1.0m),
            stale,
            strategy);

        Assert.Equal("NONE", gate.DesiredPosition);
        Assert.Equal(EntryRejection.InsufficientPriceHistory, gate.RejectionReason);
    }

    [Fact]
    public void HBAR_like_near_threshold_score_with_UNKNOWN_price_action_gets_the_precise_reason()
    {
        // HBAR/EUR at 11:52-11:58: score 0.85, clean spread, momentum and volume
        // missing, price action UNKNOWN during warm-up. The old behavior logged a
        // generic REJECT_SCORE_BELOW_THRESHOLD; the real first blocker is that the
        // exploratory path requires KNOWN positive price action.
        var strategy = Strategy();
        strategy.ExploratoryEntriesEnabled = true;

        var warmingUp = HistoryWith(samples: 1, newestUtc: Now).Assess("TEST/EUR", 6, 4, Now, 30);
        var gate = EntryGate.Evaluate(
            Signal(0.85m),
            MarketState(bid: 0.9999m, ask: 1.0001m, last: 1.0m),
            warmingUp,
            strategy);

        Assert.Equal("NONE", gate.DesiredPosition);
        Assert.Equal(EntryRejection.PriceActionUnknown, gate.RejectionReason);
    }

    [Fact]
    public void Exploratory_entries_require_KNOWN_positive_price_action()
    {
        var strategy = Strategy();
        strategy.ExploratoryEntriesEnabled = true;

        // UNKNOWN (insufficient history) -> not admitted, precise reason.
        var unknown = EntryGate.Evaluate(
            Signal(0.85m),
            MarketState(bid: 0.9999m, ask: 1.0001m, last: 1.0m),
            priceAction: null,
            strategy);
        Assert.Equal(EntryRejection.PriceActionUnknown, unknown.RejectionReason);

        // KNOWN but flat/negative -> not admitted, explicit exploratory reason.
        var flat = new PriceActionAssessment("TEST/EUR", 6, true, 1.0m, 0m, 1.0m, 2, 4, Now.AddMinutes(-25), Now);
        var refused = EntryGate.Evaluate(
            Signal(0.85m),
            MarketState(bid: 0.9999m, ask: 1.0001m, last: 1.0m),
            flat,
            strategy);
        Assert.Equal(EntryRejection.ExploratoryRequiresPositivePriceAction, refused.RejectionReason);

        // KNOWN positive -> admitted through the exploratory path.
        var admitted = EntryGate.Evaluate(
            Signal(0.85m),
            MarketState(bid: 0.9999m, ask: 1.0001m, last: 1.0m),
            Positive(),
            strategy);
        Assert.Equal("LONG_MICRO", admitted.DesiredPosition);
        Assert.True(admitted.Exploratory);
    }

    [Fact]
    public void Exploratory_entries_require_the_configured_minimum_price_action_trend()
    {
        var strategy = Strategy();
        strategy.ExploratoryEntriesEnabled = true;
        strategy.ExploratoryMinPriceActionTrendPercent = 0.50m;

        var weakRise = new PriceActionAssessment("TEST/EUR", 6, true, 1.001m, 0.20m, 0.999m, 0, 4, Now.AddMinutes(-25), Now);
        var refused = EntryGate.Evaluate(
            Signal(0.85m),
            MarketState(bid: 0.9999m, ask: 1.0001m, last: 1.0m),
            weakRise,
            strategy);
        Assert.Equal(EntryRejection.ExploratoryRequiresPositivePriceAction, refused.RejectionReason);

        var strongRise = new PriceActionAssessment("TEST/EUR", 6, true, 1.006m, 0.60m, 1.000m, 0, 4, Now.AddMinutes(-25), Now);
        var admitted = EntryGate.Evaluate(
            Signal(0.85m),
            MarketState(bid: 0.9999m, ask: 1.0001m, last: 1.0m),
            strongRise,
            strategy);
        Assert.Equal("LONG_MICRO", admitted.DesiredPosition);
        Assert.True(admitted.Exploratory);
    }

    [Fact]
    public void Extended_price_above_fast_ema_rejects_even_a_firm_entry()
    {
        var strategy = Strategy();
        strategy.MaxEntryExtensionPercent = 0.6m;

        // Firm signal, but last price is 1.0% above its own fast EMA — a chase.
        var extendedIndicators = new IndicatorSnapshot(FastEma: 0.9901m, SlowEma: 0.9850m, Rsi: 55m);
        var rejected = EntryGate.Evaluate(
            Signal(0.95m, volumeConfirmed: true),
            MarketState(bid: 0.9999m, ask: 1.0001m, last: 1.0m),
            Positive(),
            strategy,
            indicators: extendedIndicators);
        Assert.Equal("NONE", rejected.DesiredPosition);
        Assert.Equal(EntryRejection.PriceExtended, rejected.RejectionReason);

        // Price close to the fast EMA passes.
        var closeIndicators = new IndicatorSnapshot(FastEma: 0.998m, SlowEma: 0.9920m, Rsi: 55m);
        var admitted = EntryGate.Evaluate(
            Signal(0.95m, volumeConfirmed: true),
            MarketState(bid: 0.9999m, ask: 1.0001m, last: 1.0m),
            Positive(),
            strategy,
            indicators: closeIndicators);
        Assert.Equal("LONG_MICRO", admitted.DesiredPosition);
    }

    [Fact]
    public void Excessive_recent_runup_rejects_the_entry()
    {
        var strategy = Strategy();
        strategy.MaxEntryRunupPercent = 2.5m;

        // Snapshot series already gained 3% over the lookback: the move happened.
        var ranUp = new PriceActionAssessment("TEST/EUR", 6, true, 1.03m, 3.0m, 1.012m, 0, 4, Now.AddMinutes(-25), Now);
        var rejected = EntryGate.Evaluate(
            Signal(0.95m, volumeConfirmed: true),
            MarketState(bid: 1.0299m, ask: 1.0301m, last: 1.03m),
            ranUp,
            strategy);
        Assert.Equal("NONE", rejected.DesiredPosition);
        Assert.Equal(EntryRejection.PriceExtended, rejected.RejectionReason);
    }

    [Fact]
    public void Early_entries_require_widening_gap_rsi_band_and_tolerate_mild_pullback()
    {
        var strategy = Strategy();
        strategy.EarlyEntryEnabled = true;
        strategy.EarlyEntryMinScore = 0.60m;
        strategy.EarlyEntryMinEmaGapPercent = 0.10m;
        strategy.EarlyEntryMinGapVelocityPercent = 0m;
        strategy.EarlyEntryMinPriceActionTrendPercent = -0.2m;

        var indicators = new IndicatorSnapshot(FastEma: 1.0010m, SlowEma: 1.0000m, Rsi: 55m);
        // Mild pullback (-0.1%) is an entry point for the early channel, not weakness.
        var mildPullback = new PriceActionAssessment("TEST/EUR", 6, true, 0.999m, -0.10m, 1.000m, 1, 4, Now.AddMinutes(-25), Now);
        var earlySignal = Signal(
            0.60m,
            volumeConfirmed: false,
            momentum: 0.05m,
            allowsLong: false,
            emaFullyConfirmed: false,
            bullishEmaGapPercent: 0.10m,
            emaGapVelocityPercent: 0.02m);

        var admitted = EntryGate.Evaluate(
            earlySignal,
            MarketState(bid: 0.9999m, ask: 1.0001m, last: 1.0m),
            mildPullback,
            strategy,
            indicators: indicators);
        Assert.Equal("LONG_MICRO", admitted.DesiredPosition);
        Assert.True(admitted.EarlyEntry);
        Assert.False(admitted.Exploratory);

        // A gap that stopped widening (velocity <= threshold) is not an early entry.
        var shrinkingGap = earlySignal with { EmaGapVelocityPercent = -0.01m };
        var refusedVelocity = EntryGate.Evaluate(
            shrinkingGap,
            MarketState(bid: 0.9999m, ask: 1.0001m, last: 1.0m),
            mildPullback,
            strategy,
            indicators: indicators);
        Assert.Equal("NONE", refusedVelocity.DesiredPosition);
        Assert.Equal(EntryRejection.NoBullishSignal, refusedVelocity.RejectionReason);

        // RSI outside the ideal band blocks the channel.
        var refusedRsi = EntryGate.Evaluate(
            earlySignal,
            MarketState(bid: 0.9999m, ask: 1.0001m, last: 1.0m),
            mildPullback,
            strategy,
            indicators: indicators with { Rsi = 70m });
        Assert.Equal("NONE", refusedRsi.DesiredPosition);

        // A genuinely falling series (below the -0.2% tolerance) blocks it too.
        var falling = new PriceActionAssessment("TEST/EUR", 6, true, 0.995m, -0.50m, 1.000m, 3, 4, Now.AddMinutes(-25), Now);
        var refusedFalling = EntryGate.Evaluate(
            earlySignal,
            MarketState(bid: 0.9999m, ask: 1.0001m, last: 1.0m),
            falling,
            strategy,
            indicators: indicators);
        Assert.Equal("NONE", refusedFalling.DesiredPosition);
    }

    [Fact]
    public void Live_warmup_block_is_only_lifted_by_the_explicit_override()
    {
        var live = new BotConfiguration
        {
            Trading = new TradingOptions { LiveTradingEnabled = true },
            Strategy = new StrategyOptions { RequirePriceActionData = false }
        };
        InvokeNormalize(live);
        Assert.True(live.Strategy.RequirePriceActionData);

        var overridden = new BotConfiguration
        {
            Trading = new TradingOptions { LiveTradingEnabled = true },
            Strategy = new StrategyOptions
            {
                RequirePriceActionData = false,
                AllowEntriesWithoutPriceActionInLive = true
            }
        };
        InvokeNormalize(overridden);
        Assert.False(overridden.Strategy.RequirePriceActionData);
    }

    private static void InvokeNormalize(BotConfiguration config) =>
        typeof(BotConfiguration).GetMethod(
            "Normalize",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(config, null);

    // ------------------------------------------------------------ precise reasons

    [Fact]
    public void Near_threshold_score_missing_momentum_is_rejected_with_NO_MOMENTUM_CONFIRMATION()
    {
        // Exploratory disabled (e.g. live): a 0.85 with volume confirmed but no
        // momentum push must say so instead of hiding behind the score threshold.
        var strategy = Strategy();
        strategy.ExploratoryEntriesEnabled = false;

        var gate = EntryGate.Evaluate(
            Signal(0.85m, volumeConfirmed: true, momentum: 0m, volume: 0.05m),
            MarketState(bid: 0.9999m, ask: 1.0001m, last: 1.0m),
            Positive(),
            strategy);

        Assert.Equal("NONE", gate.DesiredPosition);
        Assert.Equal(EntryRejection.NoMomentumConfirmation, gate.RejectionReason);
    }

    [Fact]
    public void Near_threshold_score_missing_volume_is_rejected_with_NO_VOLUME_CONFIRMATION()
    {
        var strategy = Strategy();
        strategy.ExploratoryEntriesEnabled = false;

        var gate = EntryGate.Evaluate(
            Signal(0.85m, volumeConfirmed: false, momentum: 0.10m),
            MarketState(bid: 0.9999m, ask: 1.0001m, last: 1.0m),
            Positive(),
            strategy);

        Assert.Equal("NONE", gate.DesiredPosition);
        Assert.Equal(EntryRejection.NoVolumeConfirmation, gate.RejectionReason);
    }

    [Fact]
    public void TON_like_score_with_spread_above_the_hard_max_is_rejected_as_SPREAD_TOO_WIDE()
    {
        // TON/EUR at 12:00: score 0.85, spread 0.686% > hard max 0.5%. The spread
        // rejection fires in the hard-filter layer before any scoring logic.
        var strategy = Strategy();
        strategy.ExploratoryEntriesEnabled = true;

        var gate = EntryGate.Evaluate(
            Signal(0.85m),
            MarketState(bid: 1.582m, ask: 1.5929m, last: 1.585m), // ~0.687%
            Positive(),
            strategy);

        Assert.Equal("NONE", gate.DesiredPosition);
        Assert.Equal(EntryRejection.SpreadTooWide, gate.RejectionReason);
    }

    // -------------------------------------------------- exploratory spread limit

    [Fact]
    public void Exploratory_entry_is_refused_when_spread_exceeds_the_exploratory_limit()
    {
        // TON/EUR at 11:52: spread 0.439% is below the hard max (0.5%) but far too
        // expensive for a small sampling position (exploratory limit 0.30%).
        var strategy = Strategy();
        strategy.ExploratoryEntriesEnabled = true;

        var refused = EntryGate.Evaluate(
            Signal(0.85m),
            MarketState(bid: 1.582m, ask: 1.589m, last: 1.585m), // ~0.442%
            Positive(),
            strategy);
        Assert.Equal("NONE", refused.DesiredPosition);
        Assert.Equal(EntryRejection.SpreadTooWide, refused.RejectionReason);

        // The same candidate with a clean spread is admitted.
        var admitted = EntryGate.Evaluate(
            Signal(0.85m),
            MarketState(bid: 1.584m, ask: 1.586m, last: 1.585m), // ~0.126%
            Positive(),
            strategy);
        Assert.Equal("LONG_MICRO", admitted.DesiredPosition);
        Assert.True(admitted.Exploratory);

        // A FIRM score is not subject to the exploratory limit (hard max applies).
        var firm = EntryGate.Evaluate(
            Signal(0.92m, volumeConfirmed: true, momentum: 0.10m, volume: 0.05m),
            MarketState(bid: 1.582m, ask: 1.589m, last: 1.585m),
            Positive(),
            strategy);
        Assert.Equal("LONG_MICRO", firm.DesiredPosition);
        Assert.False(firm.Exploratory);
    }

    // ---------------------------------------------------------------- hydration

    [Fact]
    public void Hydrating_from_persisted_snapshots_makes_the_assessment_READY_immediately()
    {
        var history = new SnapshotPriceHistory();
        var records = Enumerable.Range(0, 5)
            .Select(i => new MarketSnapshotRecord(
                $"cycle{i}",
                Now.AddMinutes(-25 + i * 5),
                "TEST/EUR",
                1.0m + i * 0.001m - 0.0005m,
                1.0m + i * 0.001m + 0.0005m,
                1.0m + i * 0.001m,
                1_000_000m,
                0.5m))
            .ToList();

        // Records are fed unordered on purpose: hydration must sort by utc.
        var loaded = history.Hydrate(records.OrderByDescending(record => record.Utc));
        Assert.Equal(5, loaded);

        var assessment = history.Assess("TEST/EUR", 6, 4, Now, 30);
        Assert.NotNull(assessment);
        Assert.Equal(PriceActionAssessment.StateReady, assessment!.WarmupState);
        Assert.Equal("RISING", assessment.Direction);
    }

    [Fact]
    public void File_store_round_trips_recent_snapshots_and_filters_out_old_ones()
    {
        var options = new DryRunOptions
        {
            OutputDirectory = Path.Combine(Path.GetTempPath(), "trading-bot-tests", Guid.NewGuid().ToString("N"))
        };
        var store = new FileDryRunPortfolioStore(options);

        MarketSnapshotRecord Record(DateTimeOffset utc, decimal last) =>
            new("cycle", utc, "TEST/EUR", last - 0.0005m, last + 0.0005m, last, 1_000_000m, 0.5m);

        store.AppendMarketSnapshots([Record(Now.AddHours(-3), 0.98m)]); // pre-downtime, must be filtered
        store.AppendMarketSnapshots([Record(Now.AddMinutes(-20), 1.00m)]);
        store.AppendMarketSnapshots([Record(Now.AddMinutes(-10), 1.01m)]);

        var recent = store.LoadRecentMarketSnapshots(Now.AddMinutes(-45));

        Assert.Equal(2, recent.Count);
        Assert.All(recent, record => Assert.True(record.Utc >= Now.AddMinutes(-45)));
        Assert.Equal(recent.OrderBy(record => record.Utc).Select(r => r.Utc), recent.Select(r => r.Utc));
    }

    [Fact]
    public async Task Worker_hydrates_price_action_history_on_startup_so_the_first_cycle_is_READY()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "trading-bot-tests", Guid.NewGuid().ToString("N"));
        var config = WorkerConfig(outputDirectory, "AAA/EUR");
        config.Strategy.PriceActionHydrationMinutes = 45;
        config.Strategy.PriceActionMinSnapshots = 4;

        // A "previous worker" persisted per-cycle snapshots minutes before this
        // restart. The fake market source quotes last=1.39, so a slightly lower
        // rising series keeps the recent trend positive and non-stale.
        var seedStore = new FileDryRunPortfolioStore(config.DryRun);
        var seedUtc = DateTimeOffset.UtcNow;
        for (var i = 0; i < 4; i++)
        {
            var last = 1.386m + i * 0.001m;
            seedStore.AppendMarketSnapshots([new MarketSnapshotRecord(
                $"seed{i}",
                seedUtc.AddMinutes(-20 + i * 5),
                "AAA/EUR",
                last - 0.0005m,
                last + 0.0005m,
                last,
                1_000_000m,
                0.5m)]);
        }

        var worker = new DecisionWorker(
            config,
            new HydrationFakeMarketDataSource("AAA/EUR"),
            new HydrationFixedAdvisor("AAA/EUR"),
            new IndicatorEngine(),
            new TechnicalDecisionEngine(),
            new RiskManager(),
            new DryRunPortfolio(config.DryRun, config.Portfolio, config.ExecutionPolicy, config.PositionExit, config.PositionSizing, strategy: config.Strategy),
            broker: null);

        await worker.RunAsync(CancellationToken.None);

        var cycleLine = File.ReadLines(Path.Combine(outputDirectory, "events.jsonl")).Last();
        using var doc = JsonDocument.Parse(cycleLine);
        var diagnostics = doc.RootElement.GetProperty("entryDiagnostics");

        // The very first cycle after "restart" already has a READY price action:
        // 4 hydrated samples + the cycle's own snapshot.
        Assert.Equal(1, diagnostics.GetProperty("priceActionReadyCount").GetInt32());

        var candidate = diagnostics.GetProperty("topCandidates").EnumerateArray()
            .Single(item => item.GetProperty("pair").GetString() == "AAA/EUR");
        Assert.Equal(PriceActionAssessment.StateReady, candidate.GetProperty("priceActionState").GetString());
        Assert.Equal(5, candidate.GetProperty("priceActionSamplesAvailable").GetInt32());
        Assert.NotEqual("UNKNOWN", candidate.GetProperty("priceActionDirection").GetString());
    }

    [Fact]
    public async Task Without_hydration_the_first_cycle_is_WARMING_UP_and_says_so()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "trading-bot-tests", Guid.NewGuid().ToString("N"));
        var config = WorkerConfig(outputDirectory, "AAA/EUR");
        config.Strategy.PriceActionHydrationMinutes = 0; // hydration disabled

        var worker = new DecisionWorker(
            config,
            new HydrationFakeMarketDataSource("AAA/EUR"),
            new HydrationFixedAdvisor("AAA/EUR"),
            new IndicatorEngine(),
            new TechnicalDecisionEngine(),
            new RiskManager(),
            new DryRunPortfolio(config.DryRun, config.Portfolio, config.ExecutionPolicy, config.PositionExit, config.PositionSizing, strategy: config.Strategy),
            broker: null);

        await worker.RunAsync(CancellationToken.None);

        var cycleLine = File.ReadLines(Path.Combine(outputDirectory, "events.jsonl")).Last();
        using var doc = JsonDocument.Parse(cycleLine);
        var diagnostics = doc.RootElement.GetProperty("entryDiagnostics");

        Assert.Equal(0, diagnostics.GetProperty("priceActionReadyCount").GetInt32());

        var candidate = diagnostics.GetProperty("topCandidates").EnumerateArray()
            .Single(item => item.GetProperty("pair").GetString() == "AAA/EUR");
        Assert.Equal(PriceActionAssessment.StateWarmingUp, candidate.GetProperty("priceActionState").GetString());
        Assert.Equal(1, candidate.GetProperty("priceActionSamplesAvailable").GetInt32());
        Assert.Equal("UNKNOWN", candidate.GetProperty("priceActionDirection").GetString());
        Assert.Contains(
            "PRICE_ACTION_UNKNOWN",
            candidate.GetProperty("missingConfirmations").EnumerateArray().Select(item => item.GetString()));
    }

    // ------------------------------------------------------------- worker fixture

    private static BotConfiguration WorkerConfig(string outputDirectory, params string[] pairs) => new()
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
            PriceActionMinSnapshots = 4,
            PriceActionMaxSampleAgeMinutes = 30
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

    private sealed class HydrationFixedAdvisor(params string[] pairs) : IWatchlistAdvisor
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

    private sealed class HydrationFakeMarketDataSource(params string[] pairs) : IMarketDataSource
    {
        private readonly HashSet<string> _pairs = pairs.ToHashSet(StringComparer.OrdinalIgnoreCase);

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
                instruments.Select(instrument => State(instrument, _pairs.Contains(instrument.Pair))).ToList());

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
