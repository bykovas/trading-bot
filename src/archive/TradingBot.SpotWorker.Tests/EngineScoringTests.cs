using TradingBot.SpotWorker;
using Xunit;

namespace TradingBot.SpotWorker.Tests;

// Full TechnicalDecisionEngine tests for the two new scoring adjustments: the
// missing-volume score cap and the negative recent-price-action penalty. These run
// the REAL engine end to end (indicators + candles -> Decide), not hand-built
// TechnicalSignal records.
public class EngineScoringTests
{
    private static readonly TechnicalDecisionEngine Engine = new();

    private static StrategyOptions Strategy() => new()
    {
        MinimumEmaGapPercent = 0.35m,
        MinimumLongScore = 0.90m,
        MaxEntrySpreadPercent = 0.5m,
        MomentumLookbackBars = 4,
        MomentumMinPercent = 0.2m,
        TrendFilterMaPeriod = 50,
        VolumeConfirmationMultiple = 1.2m,
        RsiIdealMin = 45m,
        RsiIdealMax = 62m,
        NegativePriceActionPenalty = 0.05m,
        MissingVolumeScoreCap = 0.85m,
        PriceActionLookbackSnapshots = 6,
        PriceActionMinSnapshots = 4
    };

    // Bullish EMA (gap 1%), ideal RSI: +0.30 +0.15 on top of the 0.30 base.
    private static IndicatorSnapshot BullishIndicators() => new(FastEma: 1.01m, SlowEma: 1.00m, Rsi: 55m);

    // 40 flat candles with a final push: controlled volatility (+0.05), confirmed
    // momentum (+0.10), no 50-period trend data (0). With flat volume the stack
    // reaches 0.90 uncapped; a final-candle volume spike adds +0.05 -> 0.95.
    private static InstrumentMarketState MarketState(bool volumeSpike)
    {
        var candles = Enumerable.Range(0, 40)
            .Select(index =>
            {
                var close = index == 39 ? 1.005m : 1.0m;
                var volume = index == 39 && volumeSpike ? 250m : 100m;
                return new Candle(
                    DateTimeOffset.UtcNow.AddMinutes(-(40 - index) * 5),
                    Open: close,
                    High: close + 0.002m,
                    Low: close - 0.002m,
                    Close: close,
                    Volume: volume,
                    TradeCount: 10);
            })
            .ToArray();

        return new InstrumentMarketState
        {
            Instrument = new InstrumentOptions { Pair = "TEST/EUR", KrakenPair = "TESTEUR", Venue = "Kraken", Enabled = true },
            Candles = candles,
            Quote = new Quote(1.004m, 1.006m, 1.005m, 1_000_000m),
            PairRules = new PairRules("TEST/EUR", "online", 0.001m, 0.5m, 8, 5)
        };
    }

    private static DecisionProposal Decide(InstrumentMarketState marketState, StrategyOptions strategy, PriceActionAssessment? priceAction = null) =>
        Decide(marketState, BullishIndicators(), strategy, priceAction);

    private static DecisionProposal Decide(
        InstrumentMarketState marketState,
        IndicatorSnapshot indicators,
        StrategyOptions strategy,
        PriceActionAssessment? priceAction = null) =>
        Engine.Decide(
            marketState,
            indicators,
            new TradingOptions { TargetOrderEur = 10m },
            strategy,
            new PositionSizingOptions { Enabled = false },
            new RiskOptions { MaxOrderEur = 10m },
            cashEur: 100m,
            currentExposureEur: 0m,
            hasOpenPosition: false,
            priceAction);

    [Fact]
    public void Partial_bullish_ema_structure_gets_score_and_confirmations_without_opening_entry()
    {
        var strategy = Strategy();
        strategy.ExploratoryEntriesEnabled = false;
        var indicators = new IndicatorSnapshot(FastEma: 1.003m, SlowEma: 1.000m, Rsi: 55m);
        var rising = new PriceActionAssessment(
            "TEST/EUR",
            SnapshotCount: 6,
            DataSufficient: true,
            LastPrice: 1.005m,
            TrendPercent: 0.4m,
            RollingAveragePrice: 1.003m,
            ConsecutiveNonRisingSnapshots: 0);

        var proposal = Decide(MarketState(volumeSpike: true), indicators, strategy, rising);

        Assert.Equal("NONE", proposal.DesiredPosition);
        Assert.Equal(EntryRejection.NoBullishSignal, proposal.EntryRejectionReason);
        Assert.True(proposal.HasBullishStructure);
        Assert.False(proposal.EmaFullyConfirmed);
        Assert.True(proposal.Score > 0.50m);
        Assert.Contains(proposal.Contributions, c => c.Name == "Momentum" && c.Value > 0m);
        Assert.Contains(proposal.Contributions, c => c.Name == "Volume" && c.Value > 0m);
        Assert.Contains(proposal.Contributions, c => c.Name == "Trend" && c.Value >= 0m);
        Assert.True(proposal.EarlyEntryEligible);
        Assert.Equal(proposal.Score, proposal.EarlyEntryDiagnosticScore);
        Assert.Equal(10m, proposal.EarlyEntrySuggestedNotionalEur);
    }

    [Fact]
    public void Partial_bullish_ema_structure_can_open_early_entry()
    {
        var strategy = Strategy();
        strategy.EarlyEntryEnabled = true;
        strategy.EarlyEntryMinScore = 0.60m;
        strategy.EarlyEntryMinEmaGapPercent = 0.10m;
        var indicators = new IndicatorSnapshot(FastEma: 1.003m, SlowEma: 1.000m, Rsi: 55m);
        var rising = new PriceActionAssessment(
            "TEST/EUR",
            SnapshotCount: 6,
            DataSufficient: true,
            LastPrice: 1.005m,
            TrendPercent: 0.4m,
            RollingAveragePrice: 1.003m,
            ConsecutiveNonRisingSnapshots: 0);

        var proposal = Decide(MarketState(volumeSpike: true), indicators, strategy, rising);

        Assert.Equal("LONG_MICRO", proposal.DesiredPosition);
        Assert.True(proposal.EarlyEntryCandidate);
        Assert.False(proposal.ExploratoryCandidate);
        Assert.Null(proposal.EntryRejectionReason);
        Assert.True(proposal.HasBullishStructure);
        Assert.False(proposal.EmaFullyConfirmed);
        Assert.True(proposal.EarlyEntryEligible);
        // Early entries take the BASE order size, not the small sub-threshold tier.
        Assert.Equal(10m, proposal.TargetNotionalEur);
    }

    [Fact]
    public void Partial_bullish_ema_structure_does_not_open_when_exploratory_is_disabled()
    {
        var strategy = Strategy();
        strategy.ExploratoryEntriesEnabled = false;
        strategy.ExploratoryMinimumLongScore = 0.70m;
        var indicators = new IndicatorSnapshot(FastEma: 1.003m, SlowEma: 1.000m, Rsi: 55m);
        var rising = new PriceActionAssessment(
            "TEST/EUR",
            SnapshotCount: 6,
            DataSufficient: true,
            LastPrice: 1.005m,
            TrendPercent: 0.4m,
            RollingAveragePrice: 1.003m,
            ConsecutiveNonRisingSnapshots: 0);

        var proposal = Engine.Decide(
            MarketState(volumeSpike: true),
            indicators,
            new TradingOptions { LiveTradingEnabled = true, TargetOrderEur = 10m },
            strategy,
            new PositionSizingOptions { Enabled = false },
            new RiskOptions { MaxOrderEur = 10m },
            cashEur: 100m,
            currentExposureEur: 0m,
            hasOpenPosition: false,
            rising);

        Assert.Equal("NONE", proposal.DesiredPosition);
        Assert.Equal(EntryRejection.NoBullishSignal, proposal.EntryRejectionReason);
        Assert.True(proposal.EarlyEntryEligible);
    }

    [Fact]
    public void Tiny_ema_cross_still_does_not_get_early_structure_or_confirmations()
    {
        var strategy = Strategy();
        var indicators = new IndicatorSnapshot(FastEma: 1.0005m, SlowEma: 1.000m, Rsi: 55m);
        var rising = new PriceActionAssessment(
            "TEST/EUR",
            SnapshotCount: 6,
            DataSufficient: true,
            LastPrice: 1.005m,
            TrendPercent: 0.4m,
            RollingAveragePrice: 1.003m,
            ConsecutiveNonRisingSnapshots: 0);

        var proposal = Decide(MarketState(volumeSpike: true), indicators, strategy, rising);

        Assert.Equal("NONE", proposal.DesiredPosition);
        Assert.False(proposal.HasBullishStructure);
        Assert.False(proposal.EarlyEntryEligible);
        Assert.DoesNotContain(proposal.Contributions, c => c.Name == "Momentum");
        Assert.DoesNotContain(proposal.Contributions, c => c.Name == "Volume");
    }

    [Fact]
    public void High_score_without_volume_confirmation_is_capped_below_the_firm_threshold()
    {
        var proposal = Decide(MarketState(volumeSpike: false), Strategy());

        // EMA + RSI + volatility + momentum = 0.90 uncapped; the missing-volume cap
        // pulls the final score to 0.85 and the entry is rejected with the explicit
        // volume reason (NOT a generic low-score reason).
        Assert.Equal(0.85m, proposal.Score);
        Assert.Equal("NONE", proposal.DesiredPosition);
        Assert.Equal(EntryRejection.NoVolumeConfirmation, proposal.EntryRejectionReason);

        var cap = proposal.Contributions.Single(c => c.Name == "VolumeCap");
        Assert.Contains("capped at 0.85", cap.Reason);
    }

    [Fact]
    public void Same_setup_with_volume_confirmation_is_a_firm_entry()
    {
        var proposal = Decide(MarketState(volumeSpike: true), Strategy());

        Assert.Equal(0.95m, proposal.Score);
        Assert.Equal("LONG_MICRO", proposal.DesiredPosition);
        Assert.Null(proposal.EntryRejectionReason);
        Assert.DoesNotContain(proposal.Contributions, c => c.Name == "VolumeCap");
        Assert.Contains(proposal.Contributions, c => c.Name == "Volume" && c.Value > 0m);
    }

    [Fact]
    public void Negative_recent_price_action_costs_the_configured_penalty()
    {
        var falling = new PriceActionAssessment(
            "TEST/EUR",
            SnapshotCount: 6,
            DataSufficient: true,
            LastPrice: 1.005m,
            TrendPercent: -0.3m,
            RollingAveragePrice: 1.006m,
            ConsecutiveNonRisingSnapshots: 2);

        var proposal = Decide(MarketState(volumeSpike: true), Strategy(), falling);

        // 0.95 volume-confirmed stack minus the 0.05 penalty.
        Assert.Equal(0.90m, proposal.Score);
        var penalty = proposal.Contributions.Single(c => c.Name == "PriceAction");
        Assert.Equal(-0.05m, penalty.Value);
    }

    [Fact]
    public void Penalty_plus_missing_volume_cap_stack_multiplicatively_against_lagging_setups()
    {
        var falling = new PriceActionAssessment(
            "TEST/EUR",
            SnapshotCount: 6,
            DataSufficient: true,
            LastPrice: 1.005m,
            TrendPercent: -0.3m,
            RollingAveragePrice: 1.006m,
            ConsecutiveNonRisingSnapshots: 2);

        var proposal = Decide(MarketState(volumeSpike: false), Strategy(), falling);

        // 0.90 - 0.05 penalty = 0.85 uncapped, which no longer exceeds the cap; the
        // score sits at 0.85 either way and the entry is rejected.
        Assert.Equal(0.85m, proposal.Score);
        Assert.Equal("NONE", proposal.DesiredPosition);
        Assert.NotNull(proposal.EntryRejectionReason);
    }
}
