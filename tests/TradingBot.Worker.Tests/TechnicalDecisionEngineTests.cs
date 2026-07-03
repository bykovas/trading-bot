using TradingBot.Worker;
using Xunit;

namespace TradingBot.Worker.Tests;

public class TechnicalDecisionEngineTests
{
    private static readonly TradingOptions Trading = new()
    {
        TargetOrderEur = 10m
    };

    private static readonly PositionSizingOptions FixedSizing = new()
    {
        Enabled = false
    };

    private static readonly RiskOptions Risk = new()
    {
        MaxOrderEur = 15m
    };

    private static StrategyOptions Strategy(decimal minimumEmaGapPercent) => new()
    {
        MinimumEmaGapPercent = minimumEmaGapPercent,
        MinimumLongScore = 0.65m
    };

    private static InstrumentMarketState MarketState() => new()
    {
        Instrument = new InstrumentOptions { Pair = "UNI/EUR", KrakenPair = "UNIEUR", Venue = "Kraken", Enabled = true },
        Candles = Enumerable.Range(0, 30)
            .Select(index => new Candle(
                DateTimeOffset.UtcNow.AddMinutes(-index),
                Open: 100m,
                High: 100m,
                Low: 100m,
                Close: 100m,
                Volume: 100m,
                TradeCount: 10))
            .ToArray()
    };

    private static DecisionProposal Decide(decimal fastEma, decimal slowEma, decimal minimumEmaGapPercent)
    {
        return Decide(fastEma, slowEma, rsi: 50m, minimumEmaGapPercent, FixedSizing, risk: Risk, cashEur: 75m);
    }

    private static DecisionProposal Decide(
        decimal fastEma,
        decimal slowEma,
        decimal? rsi,
        decimal minimumEmaGapPercent,
        PositionSizingOptions positionSizing,
        RiskOptions risk,
        decimal cashEur,
        decimal currentExposureEur = 0m,
        bool hasOpenPosition = false)
    {
        var engine = new TechnicalDecisionEngine();
        return engine.Decide(
            MarketState(),
            new IndicatorSnapshot(fastEma, slowEma, rsi),
            Trading,
            Strategy(minimumEmaGapPercent),
            positionSizing,
            risk,
            cashEur,
            currentExposureEur,
            hasOpenPosition);
    }

    [Fact]
    public void Zero_capacity_collapses_desired_to_none_for_a_NEW_entry()
    {
        // No open position + no room left for a new entry => desired NONE (unchanged behavior).
        var sizing = new PositionSizingOptions { Enabled = true, CashReserveEur = 15m };
        var proposal = Decide(
            fastEma: 100.30m, slowEma: 100m, rsi: 55m, minimumEmaGapPercent: 0.20m,
            positionSizing: sizing,
            risk: new RiskOptions { MaxOrderEur = 10m, MaxTotalExposureEur = 50m },
            cashEur: 25m,
            currentExposureEur: 50m);

        Assert.Equal("NONE", proposal.DesiredPosition);
        Assert.Equal(0m, proposal.TargetNotionalEur);
    }

    [Fact]
    public void Risk_manager_approves_held_position_hold_instead_of_rejecting_zero_target()
    {
        // A held pair with a still-LONG signal carries target 0 by design. The risk
        // gate must record that as an approved HOLD, not "target notional is zero" —
        // otherwise the journal shows riskApproved=false on healthy positions.
        var manager = new RiskManager();
        var proposal = new DecisionProposal("NEW/EUR", "LONG_MICRO", 0.85m, 0m, Array.Empty<SignalContribution>());

        var held = manager.Evaluate(proposal, new RiskOptions { MaxOrderEur = 10m }, hasOpenPosition: true);
        Assert.True(held.Approved);
        Assert.Contains(held.Reasons, reason => reason.Contains("holding existing position"));

        var fresh = manager.Evaluate(proposal, new RiskOptions { MaxOrderEur = 10m }, hasOpenPosition: false);
        Assert.False(fresh.Approved);
        Assert.Contains(fresh.Reasons, reason => reason.Contains("target notional is zero"));
    }

    [Fact]
    public void Zero_capacity_does_NOT_collapse_desired_for_a_HELD_position()
    {
        // Same zero-capacity situation, but we already hold the pair: the signal is
        // still LONG, so desired must stay LONG_MICRO — a capacity-driven NONE would
        // masquerade as a signal flip and push the held position into the exit path.
        var sizing = new PositionSizingOptions { Enabled = true, CashReserveEur = 15m };
        var proposal = Decide(
            fastEma: 100.30m, slowEma: 100m, rsi: 55m, minimumEmaGapPercent: 0.20m,
            positionSizing: sizing,
            risk: new RiskOptions { MaxOrderEur = 10m, MaxTotalExposureEur = 50m },
            cashEur: 25m,
            currentExposureEur: 50m,
            hasOpenPosition: true);

        Assert.Equal("LONG_MICRO", proposal.DesiredPosition);
        Assert.Equal(0m, proposal.TargetNotionalEur);
        var sizingNote = Assert.Single(proposal.Contributions, contribution => contribution.Name == "PositionSizing");
        Assert.Contains("holding existing position", sizingNote.Reason);
    }

    [Fact]
    public void Ema_gap_below_threshold_has_no_ema_contribution_and_blocks_long()
    {
        var proposal = Decide(fastEma: 100.049m, slowEma: 100m, minimumEmaGapPercent: 0.05m);

        var ema = Assert.Single(proposal.Contributions, contribution => contribution.Name == "EMA");
        Assert.Equal(0m, ema.Value);
        Assert.Contains("EMA crossover ignored because gap 0.049% < configured minimum 0.050%", ema.Reason);
        Assert.Equal("NONE", proposal.DesiredPosition);
        Assert.Equal(0.55m, proposal.Score);
    }

    [Fact]
    public void Ema_gap_exactly_on_threshold_contributes_and_allows_long()
    {
        var proposal = Decide(fastEma: 100.05m, slowEma: 100m, minimumEmaGapPercent: 0.05m);

        var ema = Assert.Single(proposal.Contributions, contribution => contribution.Name == "EMA");
        Assert.Equal(0.30m, ema.Value);
        Assert.Equal("LONG_MICRO", proposal.DesiredPosition);
        Assert.Equal(0.85m, proposal.Score);
    }

    [Fact]
    public void Ema_gap_above_threshold_contributes_and_allows_long()
    {
        var proposal = Decide(fastEma: 100.051m, slowEma: 100m, minimumEmaGapPercent: 0.05m);

        var ema = Assert.Single(proposal.Contributions, contribution => contribution.Name == "EMA");
        Assert.Equal(0.30m, ema.Value);
        Assert.Equal("LONG_MICRO", proposal.DesiredPosition);
        Assert.Equal(0.85m, proposal.Score);
    }

    [Fact]
    public void Zero_threshold_disables_ema_gap_filter()
    {
        var proposal = Decide(fastEma: 100.001m, slowEma: 100m, minimumEmaGapPercent: 0m);

        var ema = Assert.Single(proposal.Contributions, contribution => contribution.Name == "EMA");
        Assert.Equal(0.30m, ema.Value);
        Assert.Equal("LONG_MICRO", proposal.DesiredPosition);
        Assert.Equal(0.85m, proposal.Score);
    }

    [Fact]
    public void Bearish_ema_gap_below_threshold_has_no_negative_ema_contribution()
    {
        var proposal = Decide(fastEma: 99.951m, slowEma: 100m, minimumEmaGapPercent: 0.05m);

        var ema = Assert.Single(proposal.Contributions, contribution => contribution.Name == "EMA");
        Assert.Equal(0m, ema.Value);
        Assert.Contains("EMA crossover ignored because gap 0.049% < configured minimum 0.050%", ema.Reason);
        Assert.Equal("NONE", proposal.DesiredPosition);
        Assert.Equal(0.55m, proposal.Score);
    }

    [Theory]
    [InlineData(70, 5, 0.70)]
    [InlineData(25, 10, 0.78)]
    [InlineData(50, 10, 0.85)]
    public void Enabled_position_sizing_selects_order_tier_from_score(decimal rsi, decimal expectedTargetEur, decimal expectedScore)
    {
        var proposal = Decide(
            fastEma: 100.2m,
            slowEma: 100m,
            rsi,
            minimumEmaGapPercent: 0.05m,
            positionSizing: EnabledSizing(),
            risk: Risk,
            cashEur: 75m);

        Assert.Equal("LONG_MICRO", proposal.DesiredPosition);
        Assert.Equal(expectedScore, proposal.Score);
        Assert.Equal(expectedTargetEur, proposal.TargetNotionalEur);
        Assert.Contains(proposal.Contributions, contribution => contribution.Name == "PositionSizing");
    }

    [Fact]
    public void Position_sizing_uses_strong_tier_for_wide_ema_gap()
    {
        var proposal = Decide(
            fastEma: 100.6m,
            slowEma: 100m,
            rsi: 50m,
            minimumEmaGapPercent: 0.05m,
            positionSizing: EnabledSizing(),
            risk: Risk,
            cashEur: 75m);

        Assert.Equal(0.85m, proposal.Score);
        Assert.Equal(15m, proposal.TargetNotionalEur);
        Assert.Contains(
            proposal.Contributions,
            contribution => contribution.Name == "PositionSizing" && contribution.Reason.Contains("EMA gap 0.6% reached strong threshold 0.5%"));
    }

    [Theory]
    [InlineData(35, 15)]
    [InlineData(30, 15)]
    [InlineData(25, 10)]
    public void Position_sizing_reduces_strong_target_to_keep_cash_reserve(decimal cashEur, decimal expectedTargetEur)
    {
        var proposal = Decide(
            fastEma: 100.6m,
            slowEma: 100m,
            rsi: 50m,
            minimumEmaGapPercent: 0.05m,
            positionSizing: EnabledSizing(),
            risk: Risk,
            cashEur);

        Assert.Equal(0.85m, proposal.Score);
        Assert.Equal(expectedTargetEur, proposal.TargetNotionalEur);
    }

    [Fact]
    public void Position_sizing_reduces_base_target_to_small_target_to_keep_cash_reserve()
    {
        var proposal = Decide(
            fastEma: 100.2m,
            slowEma: 100m,
            rsi: 25m,
            minimumEmaGapPercent: 0.05m,
            positionSizing: EnabledSizing(),
            risk: Risk,
            cashEur: 20m);

        Assert.Equal(0.78m, proposal.Score);
        Assert.Equal(5m, proposal.TargetNotionalEur);
        Assert.Contains(
            proposal.Contributions,
            contribution => contribution.Name == "PositionSizing" && contribution.Reason.Contains("reduced from EUR 10"));
    }

    [Fact]
    public void Position_sizing_allows_small_target_when_cash_after_stays_above_reserve()
    {
        var proposal = Decide(
            fastEma: 100.2m,
            slowEma: 100m,
            rsi: 70m,
            minimumEmaGapPercent: 0.05m,
            positionSizing: EnabledSizing(),
            risk: Risk,
            cashEur: 20.01m);

        Assert.Equal(0.70m, proposal.Score);
        Assert.Equal("LONG_MICRO", proposal.DesiredPosition);
        Assert.Equal(5m, proposal.TargetNotionalEur);
    }

    [Fact]
    public void Position_sizing_blocks_entry_when_cash_reserve_leaves_no_tier()
    {
        var proposal = Decide(
            fastEma: 100.2m,
            slowEma: 100m,
            rsi: 50m,
            minimumEmaGapPercent: 0.05m,
            positionSizing: EnabledSizing(),
            risk: Risk,
            cashEur: 19m);

        Assert.Equal("NONE", proposal.DesiredPosition);
        Assert.Equal(0m, proposal.TargetNotionalEur);
        Assert.Contains(
            proposal.Contributions,
            contribution => contribution.Name == "PositionSizing" && contribution.Reason.Contains("selected no entry"));
    }

    [Fact]
    public void Position_sizing_uses_lower_risk_cap_as_effective_max_order()
    {
        var proposal = Decide(
            fastEma: 100.6m,
            slowEma: 100m,
            rsi: 50m,
            minimumEmaGapPercent: 0.05m,
            positionSizing: EnabledSizing(),
            risk: new RiskOptions { MaxOrderEur = 12m },
            cashEur: 75m);

        Assert.Equal(0.85m, proposal.Score);
        Assert.Equal(10m, proposal.TargetNotionalEur);
        Assert.Contains(
            proposal.Contributions,
            contribution => contribution.Name == "PositionSizing" && contribution.Reason.Contains("max EUR 12"));
    }

    [Fact]
    public void Position_sizing_uses_total_exposure_cap_as_available_notional()
    {
        var proposal = Decide(
            fastEma: 100.6m,
            slowEma: 100m,
            rsi: 50m,
            minimumEmaGapPercent: 0.05m,
            positionSizing: EnabledSizing(),
            risk: new RiskOptions { MaxOrderEur = 15m, MaxTotalExposureEur = 35m },
            cashEur: 75m,
            currentExposureEur: 30m);

        Assert.Equal(0.85m, proposal.Score);
        Assert.Equal(5m, proposal.TargetNotionalEur);
        Assert.Contains(
            proposal.Contributions,
            contribution => contribution.Name == "PositionSizing" && contribution.Reason.Contains("max exposure EUR 35"));
    }

    [Fact]
    public void Position_sizing_blocks_entry_when_total_exposure_cap_has_no_room()
    {
        var proposal = Decide(
            fastEma: 100.6m,
            slowEma: 100m,
            rsi: 50m,
            minimumEmaGapPercent: 0.05m,
            positionSizing: EnabledSizing(),
            risk: new RiskOptions { MaxOrderEur = 15m, MaxTotalExposureEur = 35m },
            cashEur: 75m,
            currentExposureEur: 32m);

        Assert.Equal("NONE", proposal.DesiredPosition);
        Assert.Equal(0m, proposal.TargetNotionalEur);
        Assert.Contains(
            proposal.Contributions,
            contribution => contribution.Name == "PositionSizing" && contribution.Reason.Contains("exposure EUR 32"));
    }

    private static PositionSizingOptions EnabledSizing() => new()
    {
        Enabled = true,
        CashReserveEur = 15m,
        SmallOrderEur = 5m,
        BaseOrderEur = 10m,
        StrongOrderEur = 15m,
        VeryStrongOrderEur = 15m,
        MaxOrderEur = 15m,
        BaseScoreThreshold = 0.75m,
        StrongScoreThreshold = 0.88m,
        VeryStrongScoreThreshold = 0.94m,
        StrongEmaGapScoreThreshold = 0.85m,
        StrongEmaGapPercent = 0.50m
    };
}
