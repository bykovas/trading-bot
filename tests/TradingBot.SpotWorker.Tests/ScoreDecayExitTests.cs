using TradingBot.SpotWorker;
using Xunit;

namespace TradingBot.SpotWorker.Tests;

// Unit tests for the TIER 2.5 defensive exits: score decay and the post-entry
// adverse movement guard. These exist so a failed high-score entry (the OP/EUR
// pattern) is cut early instead of riding to the full stop-loss.
public class ScoreDecayExitTests
{
    private static ExecutionPolicyOptions Execution() => new()
    {
        CooldownAfterBuySeconds = 1800,
        CooldownAfterSellSeconds = 3600,
        MinHoldSeconds = 1800,
        AllowImmediateExitOnSignalFlip = false
    };

    private static PositionExitOptions Exit() => new()
    {
        MinProfitToExitOnSignalFlipPercent = 1.0m,
        StopLossPercent = 2.5m,
        TakeProfitPercent = 3.0m,
        MaxHoldMinutes = 360,
        MaxSignalFlipLossExitPercent = -1.2m,
        ScoreDecayMinEntryScore = 0.90m,
        ScoreDecayDefensiveScore = 0.50m,
        ScoreDecayDefensiveCycles = 2,
        ScoreDecayImmediateScore = 0.40m,
        PostEntryAdverseWindowMinutes = 30,
        PostEntryAdverseLossPercent = 2.0m
    };

    private static ExitEvaluation Evaluate(
        decimal pnl,
        double ageSeconds,
        ScoreDecaySnapshot? scoreDecay,
        bool priceActionNegative = false,
        bool desiredLong = true) =>
        PositionExitPolicy.EvaluateHeldPosition(
            desiredLong,
            ageSeconds,
            pnl,
            canValuePosition: true,
            killSwitchActive: false,
            Execution(),
            Exit(),
            peakPnlPercent: null,
            exitHysteresisEnabled: true,
            scoreDecay,
            priceActionNegative);

    // OP/EUR replay: entry 0.95, score later 0.40, position under water -> exit
    // immediately even though desired is still LONG and min-hold has not elapsed.
    [Fact]
    public void Immediate_score_collapse_exits_a_losing_high_conviction_position()
    {
        var result = Evaluate(
            pnl: -1.0m,
            ageSeconds: 600,
            new ScoreDecaySnapshot(EntryScore: 0.95m, CurrentScore: 0.40m, ConsecutiveLowScoreCycles: 1, ScoreConfirmsEntry: false));

        Assert.True(result.ShouldSell);
        Assert.Equal(ExitReason.ScoreDecay, result.ExitReason);
        Assert.Contains("collapsed", result.Reason);
        Assert.Equal("SELL_SCORE_DECAY", PositionExitPolicy.ExitReasonCode(result.ExitReason!.Value));
    }

    // Entry 0.95, score sat at 0.50 for 2 consecutive cycles, not profitable -> exit.
    [Fact]
    public void Persistent_low_score_exits_after_configured_cycles()
    {
        var one = Evaluate(
            pnl: -0.6m,
            ageSeconds: 600,
            new ScoreDecaySnapshot(0.95m, 0.50m, ConsecutiveLowScoreCycles: 1, ScoreConfirmsEntry: false));
        Assert.False(one.ShouldSell);

        var two = Evaluate(
            pnl: -0.6m,
            ageSeconds: 900,
            new ScoreDecaySnapshot(0.95m, 0.50m, ConsecutiveLowScoreCycles: 2, ScoreConfirmsEntry: false));
        Assert.True(two.ShouldSell);
        Assert.Equal(ExitReason.ScoreDecay, two.ExitReason);
        Assert.Contains("consecutive cycles", two.Reason);
    }

    // A PROFITABLE position is never cut by score decay: take-profit / trailing own it.
    [Fact]
    public void Score_decay_never_cuts_a_profitable_position()
    {
        var result = Evaluate(
            pnl: 0.8m,
            ageSeconds: 900,
            new ScoreDecaySnapshot(0.95m, 0.35m, ConsecutiveLowScoreCycles: 3, ScoreConfirmsEntry: false));

        Assert.False(result.ShouldSell);
    }

    // Ordinary-conviction entries (below ScoreDecayMinEntryScore) are exempt.
    [Fact]
    public void Score_decay_does_not_apply_to_low_conviction_entries()
    {
        var result = Evaluate(
            pnl: -0.6m,
            ageSeconds: 900,
            new ScoreDecaySnapshot(EntryScore: 0.85m, CurrentScore: 0.35m, ConsecutiveLowScoreCycles: 5, ScoreConfirmsEntry: false));

        Assert.False(result.ShouldSell);
    }

    // Legacy positions without a stored entry score never trigger decay exits.
    [Fact]
    public void Score_decay_stays_inert_for_legacy_positions_without_entry_score()
    {
        var result = Evaluate(
            pnl: -0.6m,
            ageSeconds: 900,
            new ScoreDecaySnapshot(EntryScore: null, CurrentScore: 0.35m, ConsecutiveLowScoreCycles: 5, ScoreConfirmsEntry: false));

        Assert.False(result.ShouldSell);
    }

    // Stop-loss (TIER 1) still outranks the defensive tier.
    [Fact]
    public void Stop_loss_still_fires_before_score_decay()
    {
        var result = Evaluate(
            pnl: -2.6m,
            ageSeconds: 600,
            new ScoreDecaySnapshot(0.95m, 0.40m, 2, false));

        Assert.True(result.ShouldSell);
        Assert.Equal(ExitReason.StopLoss, result.ExitReason);
    }

    // Post-entry adverse guard: fresh position, deeper early loss, score below the
    // defensive floor, recent price action negative, and structural deterioration
    // -> exit early. This is not a normal/tight stop-loss.
    [Fact]
    public void Post_entry_adverse_movement_exits_a_fresh_failing_position()
    {
        var result = Evaluate(
            pnl: -2.1m,
            ageSeconds: 900,
            new ScoreDecaySnapshot(
                EntryScore: 0.85m,
                CurrentScore: 0.60m,
                ConsecutiveLowScoreCycles: 0,
                ScoreConfirmsEntry: false,
                EmaBullish: false,
                MomentumPositive: true),
            priceActionNegative: true);

        Assert.True(result.ShouldSell);
        Assert.Equal(ExitReason.PostEntryAdverse, result.ExitReason);
        Assert.Equal("SELL_POST_ENTRY_ADVERSE", PositionExitPolicy.ExitReasonCode(result.ExitReason!.Value));
    }

    [Fact]
    public void Post_entry_adverse_guard_expires_after_the_window()
    {
        var result = Evaluate(
            pnl: -2.1m,
            ageSeconds: 31 * 60,
            new ScoreDecaySnapshot(0.85m, 0.60m, 0, ScoreConfirmsEntry: false, EmaBullish: false, MomentumPositive: true),
            priceActionNegative: true);

        Assert.False(result.ShouldSell);
    }

    [Fact]
    public void Post_entry_adverse_guard_holds_while_score_is_still_strong()
    {
        var result = Evaluate(
            pnl: -2.1m,
            ageSeconds: 900,
            new ScoreDecaySnapshot(0.95m, 0.85m, 0, ScoreConfirmsEntry: false, EmaBullish: true, MomentumPositive: true),
            priceActionNegative: true);

        Assert.False(result.ShouldSell);
    }

    [Fact]
    public void Post_entry_adverse_guard_does_not_sell_score_085_with_only_negative_price_action()
    {
        var result = Evaluate(
            pnl: -1.53m,
            ageSeconds: 16 * 60,
            new ScoreDecaySnapshot(
                EntryScore: 1.00m,
                CurrentScore: 0.85m,
                ConsecutiveLowScoreCycles: 0,
                ScoreConfirmsEntry: false,
                EmaBullish: true,
                MomentumPositive: true),
            priceActionNegative: true);

        Assert.False(result.ShouldSell);
        Assert.Equal("DESIRED_LONG", result.HoldReasonCode);
    }

    // End-to-end through DryRunPortfolio: the low-score counter is tracked on the
    // position across cycles and the decay exit produces a WOULD_SELL with the
    // SELL_SCORE_DECAY code (and a realized loss much smaller than stop-loss).
    [Fact]
    public void Portfolio_tracks_decay_cycles_and_sells_defensively()
    {
        var portfolio = new DryRunPortfolio(
            new DryRunOptions { Enabled = true, ApplyVirtualFills = true, TakerFeeBps = 26m, SlippageBps = 10m },
            new PortfolioOptions { StartingCashEur = 75m },
            Execution(),
            Exit(),
            new PositionSizingOptions { Enabled = false },
            new NullStore(),
            strategy: new StrategyOptions { MinimumLongScore = 0.90m, ExitEmaGapPercent = 0.15m });

        var state = new PortfolioState
        {
            CashEur = 65m,
            Positions =
            {
                new PortfolioPosition
                {
                    Pair = "OP/EUR",
                    Side = "LONG",
                    Quantity = 105m,
                    EntryPrice = 0.095m,
                    EntryNotionalEur = 10m,
                    OpenedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-20),
                    EntryScore = 0.95m
                }
            }
        };

        var marketState = new InstrumentMarketState
        {
            Instrument = new InstrumentOptions { Pair = "OP/EUR", KrakenPair = "OPEUR", Venue = "Kraken", Enabled = true },
            Candles = Enumerable.Range(0, 40)
                .Select(index => new Candle(DateTimeOffset.UtcNow.AddMinutes(-40 + index), 0.0947m, 0.0947m, 0.0947m, 0.0947m, 100m, 5))
                .ToArray(),
            Quote = new Quote(0.0946m, 0.0948m, 0.0947m, 100_000m)
        };

        var holdProposal = new DecisionProposal("OP/EUR", "LONG_MICRO", 0.50m, 0m, Array.Empty<SignalContribution>());
        var risk = new RiskEvaluation(true, new[] { "holding existing position" });

        // Cycle 1 at score 0.50: counter goes to 1, still holding.
        var first = portfolio.Apply(state, marketState, holdProposal, risk, new RiskOptions(), 0);
        Assert.Equal("WOULD_HOLD", first.Action);
        Assert.Equal(1, state.Positions.Single().LowScoreCycles);

        // Cycle 2 at score 0.50: counter reaches 2 -> defensive sell.
        var second = portfolio.Apply(state, marketState, holdProposal, risk, new RiskOptions(), 0);
        Assert.Equal("WOULD_SELL", second.Action);
        Assert.Equal("SELL_SCORE_DECAY", second.ExitReasonCode);
        Assert.Empty(state.Positions);
    }

    private sealed class NullStore : IDryRunPortfolioStore
    {
        public string StateDescription => "null";
        public string EventsDescription => "null";
        public PortfolioState? Load() => null;
        public void Save(PortfolioState state) { }
        public void AppendCycle(DryRunCycleRecord record) { }
        public void AppendMarketSnapshots(IReadOnlyList<MarketSnapshotRecord> snapshots) { }
        public IReadOnlyList<MarketSnapshotRecord> LoadRecentMarketSnapshots(DateTimeOffset sinceUtc) => [];
        public void SaveCashEvents(IReadOnlyList<PortfolioCashEvent> events) { }
    }
}
